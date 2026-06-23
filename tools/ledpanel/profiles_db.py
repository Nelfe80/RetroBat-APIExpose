# profiles_db.py
# Base de données exhaustive des profils physiques et mappings asymétriques
# Ce fichier centralise toutes les équations de mappings.md et NOMENCLATURE.md.
#
# =========================================================================
# LA PHILOSOPHIE DE LA MATRICE UNIVERSELLE (Le "Magasin de Pièces Détachées")
# =========================================================================
# L'objectif de cette structure est de détruire tout code logique (if/else) lié à 
# un émulateur spécifique dans notre programme principal (Curator).
# Tout passe par la Clé Universelle : l'ID "retropad_id".
#
# Si un profil demande d'assigner l'action sur le "retropad_id: 11" :
# 1. RETROARCH (.rmp) : Le Curator lira `retropad_id` et injectera `input_player1_btn_... = "11"`.
# 2. RETROBAT (.xml)  : Le Curator lira la colonne `retrobat` et injectera `<button controller="PAGEDOWN">`.
# 3. FBNEO (.ini)     : La version Standalone lit la colonne `dinput`. Le Curator écrira `switch ... "rightshoulder"`.
# 4. MAME (.cfg)      : La version Standalone lit la colonne `mame_btn`. Le Curator écrira `P1_BUTTON6`.
#
# =========================================================================
# EQUIVALENCE MATHEMATIQUE UNIVERSELLE (Libretro -> FBNeo/DInput -> Xbox)
# =========================================================================
# Voici la lecture humaine du tableau `SLOT_MAP` qui automatise ceci :
# 
# | RetroPad ID | Controller (RB) | FBNeo (DInput)   | MAME (.cfg) | Xbox Relatif  |
# |-------------|-----------------|------------------|-------------|---------------|
# |      0      |       B         |       a          | P1_BUTTON1  | A (Sud)       |
# |      8      |       A         |       b          | P1_BUTTON2  | B (Est)       |
# |      9      |       X         |       y          | P1_BUTTON3  | Y (Nord)      |
# |      1      |       Y         |       x          | P1_BUTTON4  | X (Ouest)     |
# |     10      |     PAGEUP      |   leftshoulder   | P1_BUTTON5  | LB (L1)       |
# |     11      |    PAGEDOWN     |  rightshoulder   | P1_BUTTON6  | RB (R1)       |
# |     12      |       L2        |   lefttrigger    | P1_BUTTON7  | LT (L2)       |
# |     13      |       R2        |   righttrigger   | P1_BUTTON8  | RT (R2)       |
# ----------------------------------------------------------------------------------

# =========================================================================
# CONSTANTES DE MAPPING (Remplaçant SLOT_MAP du curator)
# =========================================================================

SLOT_MAP = {
    # Rangée du Bas (B, A, R1, R2)
    "1": {"retrobat": "A", "retropad_id": 0, "libretro": "b", "dinput": "a", "mame_btn": "P1_BUTTON1"},
    "2": {"retrobat": "B", "retropad_id": 8, "libretro": "a", "dinput": "b", "mame_btn": "P1_BUTTON2"},
    "6": {"retrobat": "PAGEDOWN", "retropad_id": 11, "libretro": "r", "dinput": "rightshoulder", "mame_btn": "P1_BUTTON6"},
    "8": {"retrobat": "R2", "retropad_id": 13, "libretro": "r2", "dinput": "righttrigger", "mame_btn": "P1_BUTTON8"},
    
    # Rangée du Haut (Y, X, L1, L2)
    "4": {"retrobat": "Y", "retropad_id": 1, "libretro": "y", "dinput": "x", "mame_btn": "P1_BUTTON4"},
    "3": {"retrobat": "X", "retropad_id": 9, "libretro": "x", "dinput": "y", "mame_btn": "P1_BUTTON3"},
    "5": {"retrobat": "PAGEUP", "retropad_id": 10, "libretro": "l", "dinput": "leftshoulder", "mame_btn": "P1_BUTTON5"},
    "7": {"retrobat": "L2", "retropad_id": 12, "libretro": "l2", "dinput": "lefttrigger", "mame_btn": "P1_BUTTON7"},
}

SYSTEM_SLOT_MAP = {
    "start": {"retrobat": "START", "retropad_id": 3, "libretro": "start", "dinput": "start"},
    "coin": {"retrobat": "SELECT", "retropad_id": 2, "libretro": "select", "dinput": "back"},
    "select": {"retrobat": "SELECT", "retropad_id": 2, "libretro": "select", "dinput": "back"},
}

JOYSTICK_DIRECTION_MAP = {
    "up": {"retrobat": "UP", "retropad_id": 4, "libretro": "up", "dinput": "up"},
    "down": {"retrobat": "DOWN", "retropad_id": 5, "libretro": "down", "dinput": "down"},
    "left": {"retrobat": "LEFT", "retropad_id": 6, "libretro": "left", "dinput": "left"},
    "right": {"retrobat": "RIGHT", "retropad_id": 7, "libretro": "right", "dinput": "right"},
}

MAME_SLOT_KEYCODES = {
    "1": "KEYCODE_LCONTROL",
    "2": "KEYCODE_LALT",
    "3": "KEYCODE_SPACE",
    "4": "KEYCODE_LSHIFT",
    "5": "KEYCODE_Z",
    "6": "KEYCODE_X",
    "7": "KEYCODE_C",
    "8": "KEYCODE_V",
}

MAME_SLOT_JOYCODES = {
    "1": "JOYCODE_{player}_BUTTON1",
    "2": "JOYCODE_{player}_BUTTON2",
    "3": "JOYCODE_{player}_BUTTON3",
    "4": "JOYCODE_{player}_BUTTON4",
    "5": "JOYCODE_{player}_BUTTON5",
    "6": "JOYCODE_{player}_BUTTON6",
    "7": "JOYCODE_{player}_BUTTON7",
    "8": "JOYCODE_{player}_BUTTON8",
}

MAME_SYSTEM_KEYCODES = {
    "start": "KEYCODE_1",
    "select": "KEYCODE_2",
    "coin": "KEYCODE_5",
}

MAME_SYSTEM_JOYCODES = {
    # MAME standalone records Start/Coin as BUTTON9/BUTTON10, while the
    # current mame_libretro core records them as START/SELECT.
    "start": "JOYCODE_{player}_BUTTON9 OR JOYCODE_{player}_START",
    "select": "JOYCODE_{player}_BUTTON10 OR JOYCODE_{player}_SELECT",
    "coin": "JOYCODE_{player}_BUTTON10 OR JOYCODE_{player}_SELECT",
}

RMP_SLOT_BUTTONS_BY_LAYOUT = {
    "2-Button": {
        "1": "b",
        "2": "a",
    },
    "4-Button": {
        "1": "b",
        "2": "a",
        "3": "x",
        "4": "y",
    },
    "6-Button": {
        "1": "b",
        "2": "a",
        "3": "x",
        "4": "y",
        "5": "l",
        "6": "r",
    },
    "8-Button": {
        "1": "b",
        "2": "a",
        "3": "x",
        "4": "y",
        "5": "l",
        "6": "r",
        "7": "l2",
        "8": "r2",
    },
}

RMP_SYSTEM_BUTTON_MAP = {
    "start": "start",
    "select": "select",
    "coin": "select",
}


# =========================================================================
# LIBRAIRIE DES PROFILS ASYMETRIQUES
# =========================================================================

# Dictionnaire standard des correspondances de base (Fallback)
GENERIC_1_TO_1 = {
    "1": {"retropad": 0,  "label": "B",     "color": "Red"},
    "2": {"retropad": 8,  "label": "A",     "color": "Yellow"},
    "3": {"retropad": 9,  "label": "X",     "color": "Blue"},
    "4": {"retropad": 1,  "label": "Y",     "color": "Green"},
    "5": {"retropad": 10, "label": "L1",    "color": "White"},
    "6": {"retropad": 11, "label": "R1",    "color": "White"},
    "7": {"retropad": 12, "label": "L2",    "color": "Gray"},
    "8": {"retropad": 13, "label": "R2",    "color": "Gray"},
}

PROFILES_LIBRARY = {
    # ---------------------------------------------------------
    # 1. NEOGEO / FBNEO (A=Red(0), B=Yellow(8), C=Green(1), D=Blue(9))
    # ---------------------------------------------------------
    "NEOGEO_MINI": {
        "description": "Panel NeoGeo Mini (A B C D alignés courbés)",
        "slots": {
            "1": {"retropad": 0,  "label": "B", "color": "Yellow"},
            "2": {"retropad": 9,  "label": "D", "color": "Blue"},
            "3": {"retropad": 1,  "label": "C", "color": "Green"},
            "4": {"retropad": 8,  "label": "A", "color": "Red"},
            "START": {"retropad": 3, "label": "Start", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Gray"},
        }
    },
    "NEOGEO_VARIATION": {
        "description": "Haut [B][C][D], Bas [A][-][-]",
        "slots": {
            "1": {"retropad": 0, "label": "A", "color": "Red"},
            "2": {"retropad": 11, "label": "", "color": "Black"},
            "3": {"retropad": 1, "label": "C", "color": "Green"},
            "4": {"retropad": 8, "label": "B", "color": "Yellow"},
            "5": {"retropad": 9, "label": "D", "color": "Blue"},
            "6": {"retropad": 10, "label": "", "color": "Black"},
            "7": {"retropad": 13, "label": "", "color": "Black"},
            "8": {"retropad": 12, "label": "", "color": "Black"},
            "START": {"retropad": 3, "label": "START", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "COIN", "color": "Gray"},
        }
    },
    "MVS_TYPE_1": {
        "description": "Arcade NeoGeo MVS (Plat : A B C D)",
        "slots": {
            "1": {"retropad": 8,  "label": "A", "color": "Red"},
            "3": {"retropad": 0,  "label": "B", "color": "Yellow"},
            "5": {"retropad": 1,  "label": "C", "color": "Green"},
            "7": {"retropad": 9,  "label": "D", "color": "Blue"},
            "START": {"retropad": 3, "label": "Start", "color": "White"},
            "SELECT": {"retropad": 2, "label": "Coin", "color": "White"},
        }
    },
    "MVS_TYPE_1_BOTTOM": {
        "description": "Haut [-][-][-][-], Bas [A][B][C][D]",
        "slots": {
            "1": {"retropad": 0, "label": "A", "color": "Red"},
            "2": {"retropad": 8, "label": "B", "color": "Yellow"},
            "3": {"retropad": 10, "label": "", "color": "Black"},
            "4": {"retropad": 11, "label": "", "color": "Black"},
            "5": {"retropad": 12, "label": "", "color": "Black"},
            "6": {"retropad": 1, "label": "C", "color": "Green"},
            "7": {"retropad": 13, "label": "", "color": "Black"},
            "8": {"retropad": 9, "label": "D", "color": "Blue"},
            "START": {"retropad": 3, "label": "START", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "COIN", "color": "Gray"},
        }
    },
    
    # ---------------------------------------------------------
    # 2. MICRO-ORDINATEURS & 8-BITS (Basés sur GENERIC 1-TO-1 avec couleurs fixes)
    # ---------------------------------------------------------
    "APPLE_2": {
        "description": "2 Boutons Orange",
        "slots": {
            "1": {"retropad": 0, "label": "1", "color": "Orange"},
            "2": {"retropad": 8, "label": "2", "color": "Orange"},
            "START": {"retropad": 3, "label": "Start", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Gray"},
        }
    },
    "AMIGA": {
        "description": "Competition Pro (2 Boutons Red)",
        "slots": {
            "1": {"retropad": 0, "label": "B", "color": "Red"},
            "2": {"retropad": 8, "label": "A", "color": "Red"},
            "START": {"retropad": 3, "label": "Start", "color": "Blue"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Blue"},
        }
    },
    "ATARI": {
        "description": "Atari 800/ST (2 Boutons Orange)",
        "slots": {
            "1": {"retropad": 0, "label": "1", "color": "Orange"},
            "2": {"retropad": 8, "label": "2", "color": "Orange"},
            "START": {"retropad": 3, "label": "Start", "color": "Orange"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Orange"},
        }
    },
    "COMMODORE": {
        "description": "VIC-20 / C64 (1 Bouton Orange)",
        "slots": {
            "1": {"retropad": 0, "label": "1", "color": "Orange"},
            "START": {"retropad": 3, "label": "Start", "color": "Orange"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Orange"},
        }
    },
    "THOMSON": {
        "description": "MO5 / TO7 (1 Bouton Gris)",
        "slots": {
            "1": {"retropad": 0, "label": "1", "color": "Gray"},
            "START": {"retropad": 3, "label": "Start", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Gray"},
        }
    },
    "SINCLAIR": {
        "description": "ZX80 / ZX81 (1 Bouton Rouge)",
        "slots": {
            "1": {"retropad": 0, "label": "1", "color": "Red"},
            "START": {"retropad": 3, "label": "Start", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Gray"},
        }
    },
    "AMSTRAD": {
        "description": "CPC (3 Boutons : Orange, Red, Yellow)",
        "slots": {
            "1": {"retropad": 0, "label": "B", "color": "Orange"},
            "2": {"retropad": 8, "label": "A", "color": "Red"},
            "3": {"retropad": 9, "label": "X", "color": "Yellow"},
            "START": {"retropad": 3, "label": "Start", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Gray"},
        }
    },
    "MSX": {
        "description": "Sony JS-70 (2 Boutons : White, Gray)",
        "slots": {
            "1": {"retropad": 0, "label": "I",  "color": "White"},
            "2": {"retropad": 8, "label": "II", "color": "Gray"},
            "START": {"retropad": 3, "label": "Start", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Gray"},
        }
    },
    "GX4000": {
        "description": "GX4000 (2 Boutons Violet)",
        "slots": {
            "1": {"retropad": 0, "label": "B", "color": "Purple"},
            "2": {"retropad": 8, "label": "A", "color": "Purple"},
            "START": {"retropad": 3, "label": "Start", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Gray"},
        }
    },
    "NES": {
        "description": "NES Officiel : Haut White/White, Bas Red/Red",
        "slots": {
            "1": {"retropad": 0, "label": "B", "color": "Red"},
            "2": {"retropad": 8, "label": "A", "color": "Red"},
            "3": {"retropad": 9, "label": "TB", "color": "White"},
            "4": {"retropad": 1, "label": "TA", "color": "White"},
            "START": {"retropad": 3, "label": "Start", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Gray"},
        }
    },
    "MASTER_SYSTEM": {
        "description": "Master System (Tout Gris)",
        "slots": {
            "1": {"retropad": 0, "label": "1", "color": "Gray"},
            "2": {"retropad": 8, "label": "2", "color": "Gray"},
            "3": {"retropad": 9, "label": "T2", "color": "Gray"},
            "4": {"retropad": 1, "label": "T1", "color": "Gray"},
            "START": {"retropad": 3, "label": "Pause", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Gray"},
        }
    },
    "LYNX": {
        "description": "Atari Lynx (Tout Gris, inversé Xbox A/B)",
        "slots": {
            "START": {"retropad": 3, "label": "Start", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Gray"},
            "1": {"retropad": 8, "label": "A", "color": "Gray"},
            "2": {"retropad": 0, "label": "B", "color": "Gray"},
        }
    },
    "PC_ENGINE": {
        "description": "PC-Engine / TG16 Standard (Orange)",
        "slots": {
            "1": {"retropad": 8, "label": "II", "color": "Orange"},
            "2": {"retropad": 0, "label": "I",  "color": "Orange"},
            "START": {"retropad": 3, "label": "Run", "color": "Yellow"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Yellow"},
        }
    },
    "GAMEBOY": {
        "description": "GameBoy / Color (Boutons B & A en Magenta)",
        "slots": {
            "1": {"retropad": 0, "label": "B", "color": "Magenta"},
            "2": {"retropad": 8, "label": "A", "color": "Magenta"},
            "START": {"retropad": 3, "label": "Start", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Gray"},
        }
    },
    "GBA": {
        "description": "GameBoy Advance (B & A Magenta, L & R Gris)",
        "slots": {
            "1": {"retropad": 0, "label": "B", "color": "Magenta"},
            "2": {"retropad": 8, "label": "A", "color": "Magenta"},
            "5": {"retropad": 10, "label": "L", "color": "Gray"},
            "6": {"retropad": 11, "label": "R", "color": "Gray"},
            "START": {"retropad": 3, "label": "Start", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Gray"},
        }
    },
    
    # ---------------------------------------------------------
    # 3. 16-BITS (Configurations Par Défaut & Asymétriques)
    # ---------------------------------------------------------
    "SNES_DEFAULT": {
        "description": "SFC / SNES EU Officiel (Y:Vert, X:Bleu, B:Jaune, A:Rouge)",
        "slots": {
            "1": {"retropad": 0,  "label": "B", "color": "Yellow"},
            "2": {"retropad": 8,  "label": "A", "color": "Red"},
            "3": {"retropad": 9,  "label": "X", "color": "Blue"},
            "4": {"retropad": 1,  "label": "Y", "color": "Green"},
            "5": {"retropad": 10, "label": "L", "color": "Gray"},
            "6": {"retropad": 11, "label": "R", "color": "Gray"},
            "START": {"retropad": 3, "label": "Start", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Gray"},
        }
    },
    "MEGADRIVE_6B": {
        "description": "Arcade Power Stick II Sega (Haut X,Y,Z Blanc / Bas A,B,C Gris)",
        "slots": {
            "1": {"retropad": 0,  "label": "A", "color": "Gray"},
            "2": {"retropad": 8,  "label": "B", "color": "Gray"},
            "6": {"retropad": 11, "label": "C", "color": "Gray"},
            "4": {"retropad": 1,  "label": "X", "color": "White"},
            "3": {"retropad": 9,  "label": "Y", "color": "White"},
            "5": {"retropad": 10, "label": "Z", "color": "White"},
            "START": {"retropad": 3, "label": "Start", "color": "Red"},
            "SELECT": {"retropad": 2, "label": "Mode", "color": "Red"},
        }
    },
    "SNES_SCORE_MASTER": {
        "description": "Haut [L1][X][R1], Bas [Y][B][A] (Colors: W/B/W G/Y/R)",
        "slots": {
            "1": {"retropad": 1,  "label": "Y", "color": "Green"},
            "2": {"retropad": 0,  "label": "B", "color": "Yellow"},
            "3": {"retropad": 9,  "label": "X", "color": "Blue"},
            "4": {"retropad": 10, "label": "L", "color": "White"},
            "5": {"retropad": 11, "label": "R", "color": "White"},
            "6": {"retropad": 8,  "label": "A", "color": "Red"},
            "7": {"retropad": 12, "label": "",  "color": "Gray"},
            "8": {"retropad": 13, "label": "",  "color": "Gray"},
            "START": {"retropad": 3, "label": "Start", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Gray"},
        }
    },
    "SNES_SUPER_ADVANTAGE": {
        "description": "Haut [Y][X][L], Bas [B][A][R]",
        "slots": {
            "START": {"retropad": 3, "label": "Start", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Gray"},
            "1": {"retropad": 0,  "label": "B", "color": "Yellow"},
            "2": {"retropad": 8,  "label": "A", "color": "Red"},
            "3": {"retropad": 9,  "label": "X", "color": "Blue"},
            "4": {"retropad": 1,  "label": "Y", "color": "Green"},
            "5": {"retropad": 10, "label": "L", "color": "Gray"},
            "6": {"retropad": 11, "label": "R", "color": "Gray"},
            "7": {"retropad": 12, "label": "",  "color": "Gray"},
            "8": {"retropad": 13, "label": "",  "color": "Gray"},
        }
    },
    "PC_ENGINE_FIGHTING_STICK": {
        "description": "Haut [IV][V][VI] (Blue), Bas [III][II][I] (Cyan)",
        "slots": {
            "1": {"retropad": 1, "label": "III", "color": "Cyan"},
            "2": {"retropad": 0, "label": "II",  "color": "Cyan"},
            "3": {"retropad": 10,"label": "V",   "color": "Blue"},
            "4": {"retropad": 9, "label": "IV",  "color": "Blue"},
            "5": {"retropad": 11,"label": "VI",  "color": "Blue"},
            "6": {"retropad": 8, "label": "I",   "color": "Cyan"},
            "7": {"retropad": 13,"label": "",    "color": "Gray"},
            "8": {"retropad": 12,"label": "",    "color": "Gray"},
            "START": {"retropad": 3, "label": "Run", "color": "Yellow"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Yellow"},
        }
    },
    
    # ---------------------------------------------------------
    # 4. 32-BITS & MODERN CONSOLES (Defaults & Asymétriques)
    # ---------------------------------------------------------
    "PSX_DEFAULT": {
        "description": "PlayStation Default (Croix Violet, Cercle Rouge, Carré Rose, Triangle Vert/Cyan)",
        "slots": {
            "1": {"retropad": 0,  "label": "Croix",    "color": "Violet"},
            "2": {"retropad": 8,  "label": "Cercle",   "color": "Red"},
            "3": {"retropad": 9,  "label": "Triangle", "color": "Cyan"},
            "4": {"retropad": 1,  "label": "Carré",    "color": "Pink"},
            "5": {"retropad": 10, "label": "L1",       "color": "Gray"},
            "6": {"retropad": 11, "label": "R1",       "color": "Gray"},
            "7": {"retropad": 12, "label": "L2",       "color": "Gray"},
            "8": {"retropad": 13, "label": "R2",       "color": "Gray"},
            "START": {"retropad": 3, "label": "Start", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Gray"},
        }
    },
    "SATURN_DEFAULT": {
        "description": "Saturn Default (3 ou 6 Boutons standards - Xbox mapping équivalent)",
        "slots": {
            "1": {"retropad": 0,  "label": "A", "color": "Green"},
            "2": {"retropad": 8,  "label": "B", "color": "Yellow"},
            "6": {"retropad": 11, "label": "C", "color": "Blue"},
            "4": {"retropad": 1,  "label": "X", "color": "Green"},
            "3": {"retropad": 9,  "label": "Y", "color": "Yellow"},
            "5": {"retropad": 10, "label": "Z", "color": "Blue"},
            "START": {"retropad": 3, "label": "Start", "color": "Yellow"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Yellow"},
        }
    },
    "DREAMCAST_DEFAULT": {
        "description": "Dreamcast Default (A Rouge, B Jaune, X Bleu, Y Vert)",
        "slots": {
            "1": {"retropad": 0,  "label": "A", "color": "Red"},
            "2": {"retropad": 8,  "label": "B", "color": "Yellow"},
            "3": {"retropad": 9,  "label": "Y", "color": "Green"},
            "4": {"retropad": 1,  "label": "X", "color": "Blue"},
            "7": {"retropad": 12, "label": "L", "color": "Gray"},
            "8": {"retropad": 13, "label": "R", "color": "Gray"},
            "START": {"retropad": 3, "label": "Start", "color": "Yellow"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Yellow"},
        }
    },
    "N64_DEFAULT": {
        "description": "Nintendo 64 Default (A Bleu, B Vert, C Jaune, Z Gris)",
        "slots": {
            "1": {"retropad": 0,  "label": "A",  "color": "Blue"},
            "2": {"retropad": 8,  "label": "B",  "color": "Green"},
            "6": {"retropad": 11, "label": "C",  "color": "Yellow"},
            "4": {"retropad": 1,  "label": "L",  "color": "Gray"},
            "3": {"retropad": 9,  "label": "Z",  "color": "Gray"},
            "5": {"retropad": 10, "label": "R",  "color": "Gray"},
            "START": {"retropad": 3, "label": "Start", "color": "Red"},
            "SELECT": {"retropad": 2, "label": "Z",    "color": "Red"},
        }
    },
    "GAMECUBE_DEFAULT": {
        "description": "GameCube Default (A Vert, B Rouge, X Gris, Y Gris)",
        "slots": {
            "1": {"retropad": 0,  "label": "A", "color": "Green"},
            "2": {"retropad": 8,  "label": "B", "color": "Red"},
            "3": {"retropad": 9,  "label": "Y", "color": "Gray"},
            "4": {"retropad": 1,  "label": "X", "color": "Gray"},
            "5": {"retropad": 10, "label": "Z", "color": "Blue"},
            "7": {"retropad": 12, "label": "L", "color": "Gray"},
            "8": {"retropad": 13, "label": "R", "color": "Gray"},
            "START": {"retropad": 3, "label": "Start", "color": "Yellow"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Yellow"},
        }
    },
    "XBOX_DEFAULT": {
        "description": "Xbox Default (A Vert, B Rouge, X Bleu, Y Jaune)",
        "slots": {
            "1": {"retropad": 0,  "label": "A",  "color": "Green"},
            "2": {"retropad": 8,  "label": "B",  "color": "Red"},
            "3": {"retropad": 9,  "label": "Y",  "color": "Yellow"},
            "4": {"retropad": 1,  "label": "X",  "color": "Blue"},
            "5": {"retropad": 10, "label": "LB", "color": "White"},
            "6": {"retropad": 11, "label": "RB", "color": "White"},
            "7": {"retropad": 12, "label": "LT", "color": "Gray"},
            "8": {"retropad": 13, "label": "RT", "color": "Gray"},
            "START": {"retropad": 3, "label": "Start", "color": "White"},
            "SELECT": {"retropad": 2, "label": "Back", "color": "White"},
        }
    },
    "PSX_HORI_8B": {
        "description": "Fighting Stick Hori 8B",
        "slots": {
            "1": {"retropad": 12, "label": "L2", "color": "Pink"}, # L2 par défaut
            "2": {"retropad": 0,  "label": "Croix", "color": "Violet"},
            "3": {"retropad": 1,  "label": "Carré", "color": "Pink"},
            "4": {"retropad": 10, "label": "L1", "color": "Cyan"},
            "5": {"retropad": 9,  "label": "Triangle", "color": "Cyan"},
            "6": {"retropad": 8,  "label": "Cercle", "color": "Red"},
            "7": {"retropad": 11, "label": "R1", "color": "Gray"},
            "8": {"retropad": 13, "label": "R2", "color": "Gray"},
            "START": {"retropad": 3, "label": "Start", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Gray"},
        }
    },
    "N64_ARCADE_SHARK": {
        "description": "Haut [L][Z][-], Bas [B][A][R]",
        "slots": {
            "1": {"retropad": 0,  "label": "B", "color": "Blue"},
            "2": {"retropad": 11, "label": "A", "color": "White"},
            "3": {"retropad": 10, "label": "Z", "color": "White"},
            "4": {"retropad": 1,  "label": "L", "color": "Green"},
            "5": {"retropad": 12, "label": "",  "color": "Red"},
            "6": {"retropad": 13, "label": "R", "color": "White"},
            "7": {"retropad": 8,  "label": "",  "color": "Gray"},
            "8": {"retropad": 9,  "label": "",  "color": "Gray"},
            "START": {"retropad": 3, "label": "Start", "color": "Red"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Red"},
        }
    },
    "GAMECUBE_FIGHTING_STICK": {
        "description": "Officiel (A Y X Z / B L R -)",
        "slots": {
            "1": {"retropad": 0,  "label": "B", "color": "Green"},
            "2": {"retropad": 12, "label": "L", "color": "Green"},
            "3": {"retropad": 1,  "label": "Y", "color": "Green"},
            "4": {"retropad": 8,  "label": "A", "color": "Green"},
            "5": {"retropad": 9,  "label": "X", "color": "Green"},
            "6": {"retropad": 13, "label": "R", "color": "Green"},
            "7": {"retropad": 11, "label": "Z", "color": "Gray"},
            "8": {"retropad": 10, "label": "",  "color": "Gray"},
            "START": {"retropad": 3, "label": "Start", "color": "Yellow"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Yellow"},
        }
    },
    "XBOX_FIGHTING_STICK_EX": {
        "description": "Fighting Stick EX (LT X Y LB / RT A B RB)",
        "slots": {
            "1": {"retropad": 13, "label": "RT", "color": "Gray"},
            "2": {"retropad": 0,  "label": "A",  "color": "Green"},
            "3": {"retropad": 9,  "label": "X",  "color": "Blue"},
            "4": {"retropad": 12, "label": "LT", "color": "Gray"},
            "5": {"retropad": 1,  "label": "Y",  "color": "Yellow"},
            "6": {"retropad": 8,  "label": "B",  "color": "Red"},
            "7": {"retropad": 10, "label": "LB", "color": "White"},
            "8": {"retropad": 11, "label": "RB", "color": "Black"},
            "START": {"retropad": 3, "label": "Start", "color": "White"},
            "SELECT": {"retropad": 2, "label": "Back", "color": "White"},
        }
    }
,
    "3DO": {
        "description": "Profil extrait de 3do.xml",
        "slots": {
            "1": {"retropad": 8, "label": "A", "color": "Violet"},
            "2": {"retropad": 0, "label": "B", "color": "Violet"},
            "6": {"retropad": 11, "label": "PAGEDOWN", "color": "Violet"},
            "8": {"retropad": 13, "label": "R2", "color": "Gray"},
            "3": {"retropad": 9, "label": "X", "color": "Cyan"},
            "4": {"retropad": 1, "label": "Y", "color": "Cyan"},
            "5": {"retropad": 10, "label": "PAGEUP", "color": "Cyan"},
            "7": {"retropad": 12, "label": "L2", "color": "Gray"},
            "START": {"retropad": 3, "label": "START", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "COIN", "color": "Gray"},
        }
    },
    "AMIGACD32": {
        "description": "Profil extrait de amigacd32.xml",
        "slots": {
            "SELECT": {"retropad": 2, "label": "Select", "color": "Gray"},
            "1": {"retropad": 8, "label": "A", "color": "Red"},
            "2": {"retropad": 0, "label": "B", "color": "Blue"},
            "6": {"retropad": 11, "label": "PAGEDOWN", "color": "Gray"},
            "3": {"retropad": 9, "label": "X", "color": "Green"},
            "4": {"retropad": 1, "label": "Y", "color": "Yellow"},
            "5": {"retropad": 10, "label": "PAGEUP", "color": "Gray"},
            "START": {"retropad": 3, "label": "START", "color": "Gray"},
        }
    },
    "ATARI2600": {
        "description": "Profil extrait de atari2600.xml",
        "slots": {
            "1": {"retropad": 8, "label": "A", "color": "Red"},
            "6": {"retropad": 11, "label": "PAGEDOWN", "color": "Orange"},
            "5": {"retropad": 10, "label": "PAGEUP", "color": "Yellow"},
            "START": {"retropad": 3, "label": "START", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "COIN", "color": "Gray"},
        }
    },
    "ATARI5200": {
        "description": "Profil extrait de atari5200.xml",
        "slots": {
            "1": {"retropad": 0, "label": "FIRE1/KEY RETURN IN GUI", "color": "Red"},
            "2": {"retropad": 8, "label": "TRIGGER", "color": "Red"},
            "START": {"retropad": 3, "label": "CONSOL_START", "color": "Green"},
            "SELECT": {"retropad": 2, "label": "CONSOL_SELECT", "color": "Yellow"},
        }
    },
    "ATARI7800": {
        "description": "Profil extrait de atari7800.xml",
        "slots": {
            "1": {"retropad": 8, "label": "A", "color": "Red"},
            "2": {"retropad": 0, "label": "B", "color": "Red"},
            "START": {"retropad": 3, "label": "START", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "COIN", "color": "Gray"},
        }
    },
    "ATOMISWAVE": {
        "description": "Profil extrait de atomiswave.xml",
        "slots": {
            "SELECT": {"retropad": 2, "label": "Select", "color": "Gray"},
            "1": {"retropad": 8, "label": "A", "color": "Green"},
            "2": {"retropad": 0, "label": "B", "color": "Green"},
            "6": {"retropad": 11, "label": "PAGEDOWN", "color": "Green"},
            "3": {"retropad": 9, "label": "X", "color": "Green"},
            "4": {"retropad": 1, "label": "Y", "color": "Green"},
            "5": {"retropad": 10, "label": "PAGEUP", "color": "Green"},
            "START": {"retropad": 3, "label": "START", "color": "Orange"},
        }
    },
    "BANDAIWONDERSWAN": {
        "description": "Profil extrait de bandaiwonderswan.xml",
        "slots": {
            "1": {"retropad": 8, "label": "A", "color": "White"},
            "2": {"retropad": 0, "label": "B", "color": "White"},
            "6": {"retropad": 12, "label": "PAGEDOWN", "color": "Grey"},
            "8": {"retropad": 11, "label": "R2", "color": "Grey"},
            "5": {"retropad": 10, "label": "PAGEUP", "color": "Grey"},
            "7": {"retropad": 13, "label": "L2", "color": "Grey"},
            "START": {"retropad": 3, "label": "START", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "COIN", "color": "Gray"},
        }
    },
    "BBCMICRO": {
        "description": "Profil extrait de bbcmicro.xml",
        "slots": {
            "START": {"retropad": 3, "label": "Start", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Gray"},
            "1": {"retropad": 8, "label": "A", "color": "Red"},
            "2": {"retropad": 0, "label": "B", "color": "Red"},
        }
    },
    "CDI": {
        "description": "Profil extrait de cdi.xml",
        "slots": {
            "START": {"retropad": 3, "label": "Start", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Gray"},
            "1": {"retropad": 8, "label": "A", "color": "Gray"},
            "2": {"retropad": 0, "label": "B", "color": "Gray"},
            "3": {"retropad": 9, "label": "X", "color": "Gray"},
            "4": {"retropad": 1, "label": "Y", "color": "Gray"},
        }
    },
    "COLECOVISION": {
        "description": "Profil extrait de colecovision.xml",
        "slots": {
            "START": {"retropad": 3, "label": "Start", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Gray"},
            "1": {"retropad": 8, "label": "A", "color": "Gray"},
            "2": {"retropad": 0, "label": "B", "color": "Gray"},
            "6": {"retropad": 11, "label": "PAGEDOWN", "color": "Gray"},
            "8": {"retropad": 13, "label": "R2", "color": "Gray"},
            "3": {"retropad": 9, "label": "X", "color": "Gray"},
            "4": {"retropad": 1, "label": "Y", "color": "Gray"},
            "5": {"retropad": 10, "label": "PAGEUP", "color": "Gray"},
            "7": {"retropad": 12, "label": "L2", "color": "Gray"},
        }
    },
    "FBNEO": {
        "description": "Profil extrait de fbneo.xml",
        "slots": {
            "3": {"retropad": 8, "label": "X", "color": "Red"},
            "4": {"retropad": 0, "label": "Y", "color": "Yellow"},
            "5": {"retropad": 1, "label": "PAGEUP", "color": "Green"},
            "7": {"retropad": 9, "label": "L2", "color": "Blue"},
            "START": {"retropad": 3, "label": "START", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "COIN", "color": "Gray"},
        }
    },
    "JAGUAR": {
        "description": "Profil extrait de jaguar.xml",
        "slots": {
            "1": {"retropad": 0, "label": "A", "color": "Red"},
            "2": {"retropad": 1, "label": "B", "color": "Red"},
            "6": {"retropad": 8, "label": "PAGEDOWN", "color": "Red"},
            "8": {"retropad": 13, "label": "R2", "color": "Gray"},
            "3": {"retropad": 9, "label": "X", "color": "Red"},
            "4": {"retropad": 10, "label": "Y", "color": "Red"},
            "5": {"retropad": 11, "label": "PAGEUP", "color": "Red"},
            "7": {"retropad": 12, "label": "L2", "color": "Gray"},
            "START": {"retropad": 3, "label": "START", "color": "Grey"},
            "SELECT": {"retropad": 2, "label": "COIN", "color": "Grey"},
        }
    },
    "MAME": {
        "description": "Profil extrait de mame.xml",
        "slots": {
            "1": {"retropad": 8, "label": "A", "color": "White"},
            "2": {"retropad": 0, "label": "B", "color": "White"},
            "6": {"retropad": 11, "label": "PAGEDOWN", "color": "Gray"},
            "8": {"retropad": 13, "label": "R2", "color": "Gray"},
            "3": {"retropad": 9, "label": "X", "color": "Gray"},
            "4": {"retropad": 1, "label": "Y", "color": "Gray"},
            "5": {"retropad": 10, "label": "PAGEUP", "color": "Gray"},
            "7": {"retropad": 12, "label": "L2", "color": "Gray"},
            "START": {"retropad": 3, "label": "START", "color": "White"},
            "SELECT": {"retropad": 2, "label": "COIN", "color": "White"},
        }
    },
    "MEGADUCK": {
        "description": "Profil extrait de megaduck.xml",
        "slots": {
            "1": {"retropad": 8, "label": "A", "color": "Red"},
            "2": {"retropad": 0, "label": "B", "color": "Red"},
            "START": {"retropad": 3, "label": "START", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "COIN", "color": "Gray"},
        }
    },
    "MULTIVISION": {
        "description": "Profil extrait de multivision.xml",
        "slots": {
            "START": {"retropad": 3, "label": "Start", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Gray"},
            "1": {"retropad": 8, "label": "A", "color": "Red"},
            "2": {"retropad": 0, "label": "B", "color": "Red"},
        }
    },
    "NAOMI": {
        "description": "Profil extrait de naomi.xml",
        "slots": {
            "1": {"retropad": 8, "label": "A", "color": "Red"},
            "2": {"retropad": 0, "label": "B", "color": "Red"},
            "6": {"retropad": 11, "label": "PAGEDOWN", "color": "Red"},
            "3": {"retropad": 9, "label": "X", "color": "Blue"},
            "4": {"retropad": 1, "label": "Y", "color": "Blue"},
            "5": {"retropad": 12, "label": "PAGEUP", "color": "Blue"},
            "START": {"retropad": 3, "label": "START", "color": "White"},
            "SELECT": {"retropad": 2, "label": "COIN", "color": "White"},
        }
    },
    "NAOMIGD": {
        "description": "Profil extrait de naomigd.xml",
        "slots": {
            "1": {"retropad": 8, "label": "A", "color": "Red"},
            "2": {"retropad": 0, "label": "B", "color": "Red"},
            "6": {"retropad": 11, "label": "PAGEDOWN", "color": "Red"},
            "3": {"retropad": 9, "label": "X", "color": "Blue"},
            "4": {"retropad": 1, "label": "Y", "color": "Blue"},
            "5": {"retropad": 12, "label": "PAGEUP", "color": "Blue"},
            "START": {"retropad": 3, "label": "START", "color": "White"},
            "SELECT": {"retropad": 2, "label": "COIN", "color": "White"},
        }
    },
    "NDS": {
        "description": "Profil extrait de nds.xml",
        "slots": {
            "1": {"retropad": 8, "label": "A", "color": "Gray"},
            "2": {"retropad": 0, "label": "B", "color": "Gray"},
            "6": {"retropad": 11, "label": "R", "color": "Gray"},
            "3": {"retropad": 9, "label": "X", "color": "Gray"},
            "4": {"retropad": 1, "label": "Y", "color": "Gray"},
            "5": {"retropad": 10, "label": "L", "color": "Gray"},
            "START": {"retropad": 3, "label": "Start", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Gray"},
        }
    },
    "NEOGEO": {
        "description": "Profil extrait de neogeo.xml",
        "slots": {
            "3": {"retropad": 8, "label": "X", "color": "Red"},
            "4": {"retropad": 0, "label": "Y", "color": "Yellow"},
            "5": {"retropad": 1, "label": "PAGEUP", "color": "Green"},
            "7": {"retropad": 9, "label": "L2", "color": "Blue"},
            "START": {"retropad": 3, "label": "START", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "COIN", "color": "Gray"},
        }
    },
    "NEOGEOCD": {
        "description": "Profil extrait de neogeocd.xml",
        "slots": {
            "1": {"retropad": 0, "label": "A", "color": "Red"},
            "2": {"retropad": 8, "label": "B", "color": "Red"},
            "3": {"retropad": 10, "label": "", "color": "Black"},
            "4": {"retropad": 11, "label": "", "color": "Black"},
            "5": {"retropad": 12, "label": "", "color": "Black"},
            "6": {"retropad": 1, "label": "C", "color": "Grey"},
            "7": {"retropad": 13, "label": "", "color": "Black"},
            "8": {"retropad": 9, "label": "D", "color": "Grey"},
            "START": {"retropad": 3, "label": "START", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "COIN", "color": "Gray"},
        }
    },
    "NGP": {
        "description": "Profil extrait de ngp.xml",
        "slots": {
            "SELECT": {"retropad": 2, "label": "Select", "color": "Gray"},
            "1": {"retropad": 8, "label": "A", "color": "Gray"},
            "2": {"retropad": 0, "label": "B", "color": "Gray"},
            "START": {"retropad": 3, "label": "START", "color": "Gray"},
        }
    },
    "NGPC": {
        "description": "Profil extrait de ngpc.xml",
        "slots": {
            "SELECT": {"retropad": 2, "label": "Select", "color": "Gray"},
            "1": {"retropad": 8, "label": "A", "color": "Gray"},
            "2": {"retropad": 0, "label": "B", "color": "Gray"},
            "START": {"retropad": 3, "label": "START", "color": "Gray"},
        }
    },
    "ORICATMOS": {
        "description": "Profil extrait de oricatmos.xml",
        "slots": {
            "START": {"retropad": 3, "label": "Start", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Gray"},
            "1": {"retropad": 8, "label": "A", "color": "Red"},
        }
    },
    "POKEMINI": {
        "description": "Profil extrait de pokemini.xml",
        "slots": {
            "START": {"retropad": 3, "label": "Start", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Gray"},
            "1": {"retropad": 8, "label": "A", "color": "Blue"},
            "2": {"retropad": 0, "label": "B", "color": "Blue"},
        }
    },
    "SAMCOUPE": {
        "description": "Profil extrait de samcoupe.xml",
        "slots": {
            "START": {"retropad": 3, "label": "Start", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Gray"},
            "1": {"retropad": 8, "label": "A", "color": "Red"},
            "2": {"retropad": 0, "label": "B", "color": "Red"},
        }
    },
    "SCUMMVM": {
        "description": "Profil extrait de scummvm.xml",
        "slots": {
            "START": {"retropad": 3, "label": "Start", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Gray"},
            "1": {"retropad": 8, "label": "Left Mouse Button", "color": "Gray"},
        }
    },
    "SCV": {
        "description": "Profil extrait de scv.xml",
        "slots": {
            "START": {"retropad": 3, "label": "Start", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Gray"},

        }
    },
    "SG1000": {
        "description": "Profil extrait de sg1000.xml",
        "slots": {
            "START": {"retropad": 3, "label": "Start", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Gray"},
            "1": {"retropad": 8, "label": "A", "color": "Red"},
            "2": {"retropad": 0, "label": "B", "color": "Red"},
        }
    },
    "SNKNEOGEO": {
        "description": "Profil extrait de snkneogeo.xml",
        "slots": {
            "3": {"retropad": 8, "label": "X", "color": "Red"},
            "4": {"retropad": 0, "label": "Y", "color": "Yellow"},
            "5": {"retropad": 1, "label": "PAGEUP", "color": "Green"},
            "7": {"retropad": 9, "label": "L2", "color": "Blue"},
            "START": {"retropad": 3, "label": "START", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "COIN", "color": "Gray"},
        }
    },
    "SPECTRAVIDEO": {
        "description": "Profil extrait de spectravideo.xml",
        "slots": {
            "START": {"retropad": 3, "label": "Start", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Gray"},
            "1": {"retropad": 8, "label": "A", "color": "Red"},
            "2": {"retropad": 0, "label": "B", "color": "Red"},
        }
    },
    "SUPERVISION": {
        "description": "Profil extrait de supervision.xml",
        "slots": {
            "1": {"retropad": 8, "label": "A", "color": "Lime"},
            "2": {"retropad": 0, "label": "B", "color": "Lime"},
            "START": {"retropad": 3, "label": "START", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "COIN", "color": "Gray"},
        }
    },
    "TI994A": {
        "description": "Profil extrait de ti994a.xml",
        "slots": {
            "1": {"retropad": 8, "label": "A", "color": "Red"},
            "2": {"retropad": 0, "label": "B", "color": "Red"},
            "START": {"retropad": 3, "label": "START", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "COIN", "color": "Gray"},
        }
    },
    "TIC80": {
        "description": "Profil extrait de tic80.xml",
        "slots": {
            "START": {"retropad": 3, "label": "Start", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Gray"},
            "1": {"retropad": 8, "label": "A", "color": "Green"},
            "2": {"retropad": 0, "label": "B", "color": "Red"},
            "3": {"retropad": 9, "label": "X", "color": "Blue"},
            "4": {"retropad": 1, "label": "Y", "color": "Orange"},
        }
    },
    "TRS80COCO": {
        "description": "Profil extrait de trs80coco.xml",
        "slots": {
            "START": {"retropad": 3, "label": "Start", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Gray"},
            "1": {"retropad": 8, "label": "A", "color": "Gray"},
            "2": {"retropad": 0, "label": "B", "color": "Red"},
        }
    },
    "UZEBOX": {
        "description": "Profil extrait de uzebox.xml",
        "slots": {
            "1": {"retropad": 8, "label": "A", "color": "Yellow"},
            "2": {"retropad": 0, "label": "B", "color": "Red"},
            "6": {"retropad": 11, "label": "PAGEDOWN", "color": "Gray"},
            "3": {"retropad": 9, "label": "X", "color": "Green"},
            "4": {"retropad": 1, "label": "Y", "color": "Blue"},
            "5": {"retropad": 10, "label": "PAGEUP", "color": "Gray"},
            "START": {"retropad": 3, "label": "START", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "COIN", "color": "Gray"},
        }
    },
    "VECTREX": {
        "description": "Profil extrait de vectrex.xml",
        "slots": {
            "START": {"retropad": 3, "label": "Start", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Gray"},
            "1": {"retropad": 8, "label": "1", "color": "Red"},
            "2": {"retropad": 0, "label": "2", "color": "Red"},
            "3": {"retropad": 9, "label": "3", "color": "Red"},
            "4": {"retropad": 1, "label": "4", "color": "Red"},
        }
    },
    "VG5000": {
        "description": "Profil extrait de vg5000.xml",
        "slots": {
            "START": {"retropad": 3, "label": "Start", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Gray"},
            "1": {"retropad": 8, "label": "A", "color": "Red"},
        }
    },
    "VIDEOPACPLUS": {
        "description": "Profil extrait de videopacplus.xml",
        "slots": {
            "START": {"retropad": 3, "label": "Start", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Gray"},
            "1": {"retropad": 8, "label": "A", "color": "Red"},
        }
    },
    "VIRTUALBOY": {
        "description": "Profil extrait de virtualboy.xml",
        "slots": {
            "1": {"retropad": 8, "label": "A", "color": "Red"},
            "2": {"retropad": 0, "label": "B", "color": "Red"},
            "START": {"retropad": 3, "label": "START", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "COIN", "color": "Gray"},
        }
    },
    "WII": {
        "description": "Profil extrait de wii.xml",
        "slots": {
            "1": {"retropad": 8, "label": "A", "color": "White"},
            "2": {"retropad": 0, "label": "B", "color": "White"},
            "6": {"retropad": 11, "label": "Z", "color": "White"},
            "8": {"retropad": 13, "label": "R", "color": "White"},
            "3": {"retropad": 9, "label": "X", "color": "White"},
            "4": {"retropad": 1, "label": "Y", "color": "White"},
            "5": {"retropad": 10, "label": "PAGEUP", "color": "White"},
            "7": {"retropad": 12, "label": "L", "color": "White"},
            "START": {"retropad": 3, "label": "Start", "color": "White"},
            "SELECT": {"retropad": 2, "label": "COIN", "color": "White"},
        }
    },
    "WSWAN": {
        "description": "Profil extrait de wswan.xml",
        "slots": {
            "1": {"retropad": 8, "label": "A", "color": "White"},
            "2": {"retropad": 0, "label": "B", "color": "White"},
            "6": {"retropad": 12, "label": "PAGEDOWN", "color": "Grey"},
            "8": {"retropad": 11, "label": "R2", "color": "Grey"},
            "5": {"retropad": 10, "label": "PAGEUP", "color": "Grey"},
            "7": {"retropad": 13, "label": "L2", "color": "Grey"},
            "START": {"retropad": 3, "label": "START", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "COIN", "color": "Gray"},
        }
    },
    "WSWANC": {
        "description": "Profil extrait de wswanc.xml",
        "slots": {
            "1": {"retropad": 8, "label": "A", "color": "White"},
            "2": {"retropad": 0, "label": "B", "color": "White"},
            "6": {"retropad": 12, "label": "PAGEDOWN", "color": "Grey"},
            "8": {"retropad": 11, "label": "R2", "color": "Grey"},
            "5": {"retropad": 10, "label": "PAGEUP", "color": "Grey"},
            "7": {"retropad": 13, "label": "L2", "color": "Grey"},
            "START": {"retropad": 3, "label": "START", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "COIN", "color": "Gray"},
        }
    },
    "X1": {
        "description": "Profil extrait de x1.xml",
        "slots": {
            "1": {"retropad": 8, "label": "A", "color": "Red"},
            "2": {"retropad": 0, "label": "B", "color": "Red"},
            "START": {"retropad": 3, "label": "START", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "COIN", "color": "Gray"},
        }
    },
    "X68000": {
        "description": "Profil extrait de x68000.xml",
        "slots": {
            "START": {"retropad": 3, "label": "Start", "color": "Gray"},
            "SELECT": {"retropad": 2, "label": "Select", "color": "Gray"},
            "1": {"retropad": 8, "label": "A", "color": "Red"},
            "2": {"retropad": 0, "label": "B", "color": "Red"},
        }
    },
}

SYSTEM_TO_PROFILE_MAP = {
    "3do": "3DO",
    "amigacd32": "AMIGACD32",
    "atari2600": "ATARI2600",
    "atari5200": "ATARI5200",
    "atari7800": "ATARI7800",
    "atomiswave": "ATOMISWAVE",
    "bandaiwonderswan": "BANDAIWONDERSWAN",
    "bbcmicro": "BBCMICRO",
    "cdi": "CDI",
    "colecovision": "COLECOVISION",
    "fbneo": "FBNEO",
    "jaguar": "JAGUAR",
    "mame": "MAME",
    "megaduck": "MEGADUCK",
    "multivision": "MULTIVISION",
    "naomi": "NAOMI",
    "naomigd": "NAOMIGD",
    "nds": "NDS",
    "neogeo": "NEOGEO",
    "neogeocd": "NEOGEOCD",
    "ngp": "NGP",
    "ngpc": "NGPC",
    "oricatmos": "ORICATMOS",
    "pokemini": "POKEMINI",
    "samcoupe": "SAMCOUPE",
    "scummvm": "SCUMMVM",
    "scv": "SCV",
    "sg1000": "SG1000",
    "snkneogeo": "SNKNEOGEO",
    "spectravideo": "SPECTRAVIDEO",
    "supervision": "SUPERVISION",
    "ti994a": "TI994A",
    "tic80": "TIC80",
    "trs80coco": "TRS80COCO",
    "uzebox": "UZEBOX",
    "vectrex": "VECTREX",
    "vg5000": "VG5000",
    "videopacplus": "VIDEOPACPLUS",
    "virtualboy": "VIRTUALBOY",
    "wii": "WII",
    "wswan": "WSWAN",
    "wswanc": "WSWANC",
    "x1": "X1",
    "x68000": "X68000",

    # Micro-ordinateurs
    "amiga": "AMIGA", "amiga500": "AMIGA", "amiga600": "AMIGA", "amiga1200": "AMIGA",
    "apple2": "APPLE_2", "apple2gs": "APPLE_2",
    "atari800": "ATARI", "atarist": "ATARI",
    "c20": "COMMODORE", "c64": "COMMODORE", "c128": "COMMODORE", "vic20": "COMMODORE",
    "thomson": "THOMSON",
    "zx81": "SINCLAIR", "zxspectrum": "SINCLAIR",
    "amstradcpc": "AMSTRAD",
    "msx": "MSX", "msx1": "MSX", "msx2": "MSX", "msx2+": "MSX", "msxturbor": "MSX",
    "gx4000": "GX4000",
    
    # Consoles 8-Bits
    "nes": "NES", "fds": "NES",
    "mastersystem": "MASTER_SYSTEM", "gamegear": "MASTER_SYSTEM",
    "gb": "GAMEBOY", "gbc": "GAMEBOY",
    "lynx": "LYNX",
    "pcenginecd": "PC_ENGINE", "necpcengine": "PC_ENGINE", "pcfx": "PC_ENGINE", "supergrafx": "PC_ENGINE",
    
    # Consoles 16-Bits et Portables Modernes
    "snes": "SNES_DEFAULT", "satellaview": "SNES_DEFAULT", "sufami": "SNES_DEFAULT",
    "segamegadrive": "MEGADRIVE_6B", "sega32x": "MEGADRIVE_6B", "segacd": "MEGADRIVE_6B",
    "gba": "GBA",
    
    # Consoles 32-Bits et Modernes
    "psx": "PSX_DEFAULT", "ps2": "PSX_DEFAULT", "psp": "PSX_DEFAULT",
    "n64": "N64_DEFAULT",
    "saturn": "SATURN_DEFAULT",
    "dreamcast": "DREAMCAST_DEFAULT",
    "gamecube": "GAMECUBE_DEFAULT",
    "xbox": "XBOX_DEFAULT", "xbox360": "XBOX_DEFAULT",
    
    # Notes de fonctionnement Curator :
    # Les systèmes purement arcade (mame, fbneo, atomiswave, naomi) 
    # ou les micro-consoles ne disposant d'aucune identité lettrée (lynx, pcengine)
    # basculeront par défaut sur la matrice GENERIC_1_TO_1.
}

