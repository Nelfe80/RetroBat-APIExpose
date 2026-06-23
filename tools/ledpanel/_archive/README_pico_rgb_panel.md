Pico RGB Panel Firmware (SJ@JX Buttons)
=======================================

This firmware allows you to control SJ@JX RGB arcade buttons using a Raspberry Pi Pico.

It supports:

-   8 RGB buttons
-   Smart PWM (with conflict detection)
-   Panel profiles (NeoGeo, SNES, etc.)
-   USB serial control
-   Real-time updates from a PC application

* * * * *

Hardware Wiring (SJ@JX Buttons)
===============================

Required hardware
-----------------

-   SJ@JX RGB arcade buttons
-   JST-4 to Dupont cables
-   Raspberry Pi Pico (or compatible board)
-   USB cable

* * * * *

Cable color mapping (IMPORTANT)
-------------------------------

Each SJ@JX button uses a 4-wire cable:

-   Yellow (B) → GPIO
-   White (G) → GPIO
-   Red (+) → GPIO
-   Black (R) → 3.3V

IMPORTANT:

Black wire MUST be connected to 3.3V\

Do NOT connect it to a GPIO

* * * * *

Full wiring table
-----------------

Slot 1 (B1):\

Yellow → GP0\

White → GP1\

Red → GP2\

Black → 3.3V

Slot 2 (B2):\

Yellow → GP3\

White → GP4\

Red → GP5\

Black → 3.3V

Slot 3 (B3):\

Yellow → GP6\

White → GP7\

Red → GP8\

Black → 3.3V

Slot 4 (B4):\

Yellow → GP9\

White → GP10\

Red → GP11\

Black → 3.3V

Slot 5 (B5):\

Yellow → GP12\

White → GP13\

Red → GP14\

Black → 3.3V

Slot 6 (B6):\

Yellow → GP15\

White → GP16\

Red → GP17\

Black → 3.3V

Slot 7 (B7):\

Yellow → GP18\

White → GP19\

Red → GP20\

Black → 3.3V

Slot 8 (B8):\

Yellow → GP21\

White → GP22\

Red → GP26\

Black → 3.3V

* * * * *

Physical panel layout
---------------------

Top row:    4 3 5 7\

Bottom row: 1 2 6 8

* * * * *

Electrical behavior
-------------------

The LEDs use inverted logic:

-   0 = ON
-   1 = OFF

So:

-   WHITE = all channels ON
-   BLACK = all channels OFF

* * * * *

Color System
============

Primary colors (always safe)
----------------------------

These do NOT use PWM and are 100 percent stable:

-   WHITE
-   PINK
-   CYAN
-   YELLOW
-   BLUE
-   RED
-   GREEN
-   BLACK

* * * * *

PWM colors (shades)
-------------------

These use PWM and may fallback if conflicts are detected:

-   ORANGE
-   PURPLE
-   VIOLET
-   LIME
-   GOLD
-   TEAL
-   MAGENTA
-   GRAY

* * * * *

PWM Limitation (Important)
==========================

The Raspberry Pi Pico has limited PWM channels.

Some GPIOs share PWM hardware internally.

This means:

-   changing one pin can affect another
-   not all buttons can display independent shades at the same time

* * * * *

Smart behavior in firmware
--------------------------

The firmware automatically:

-   detects PWM conflicts
-   falls back to a safe primary color
-   keeps the system stable

Example:

ORANGE may fallback to RED if unsafe

* * * * *

Forced PWM
----------

You can force PWM manually:

SETPWM B1 ORANGE

Use this ONLY when:

-   other buttons are OFF (BLACK)
-   or conflicts are visually irrelevant

* * * * *

Installing MicroPython
======================

1.  Go to micropython.org/download/rp2-pico
2.  Download the UF2 file
3.  Hold BOOTSEL on the Pico
4.  Plug it into USB
5.  A drive named RPI-RP2 appears
6.  Drag and drop the UF2 file

The Pico will reboot with MicroPython

* * * * *

Installing Thonny
=================

1.  Download from thonny.org
2.  Install and launch

* * * * *

Configure Thonny
----------------

Go to:

Tools → Options → Interpreter

Select:

MicroPython (Raspberry Pi Pico)

Choose the correct COM port

* * * * *

Uploading the Firmware
======================

1.  Open main.py
2.  Save to Pico as main.py
3.  Open profiles_db.py
4.  Save to Pico as profiles_db.py

* * * * *

Running the Firmware
====================

The Pico automatically runs main.py at boot.

You should see:

READY PROFILE DRIVER

* * * * *

Commands
========

Basic
-----

PING → PONG\

SCAN → list system info\

GET → current button states

* * * * *

Button control
--------------

SET B1 RED\

SET B2 BLUE

ALL GREEN\

CLEAR

* * * * *

Slot control
------------

SLOT 4 RED\

SLOT 1 BLUE

* * * * *

Panel profiles
--------------

PANELS → list profiles

PANEL NEOGEO\

PROFILE SNES

* * * * *

Batch commands
--------------

BATCH B1 RED;B2 BLUE;B3 GREEN

* * * * *

PWM commands
------------

SETPWM B1 ORANGE

BATCHPWM B1 ORANGE;B2 PURPLE

* * * * *

Demo commands
-------------

DEMOPANELS → cycles through all panels

DEMOBUTTONS → shows buttons 1 to 8

* * * * *

Profiles System
===============

Profiles are defined in profiles_db.py.

Example:

PROFILES_LIBRARY = {\

"NEOGEO": {\

"slots": {\

"1": {"color": "GREEN"},\

"2": {"color": "RED"},\

"3": {"color": "BLUE"},\

"4": {"color": "ORANGE"}\

}\

}\

}

* * * * *

Main Functions (Overview)
=========================

set_button(btn, color)\

→ sets a button color

set_slot(slot, color)\

→ maps slot to button

apply_profile(name)\

→ loads a panel

pwm_is_safe()\

→ checks PWM conflicts

write_channel()\

→ writes GPIO or PWM

clear()\

→ turns everything off

demo_panels()\

→ cycles profiles

demo_buttons()\

→ tests button positions

* * * * *

Typical Use Case
================

NeoGeo panel:

CLEAR\

PANEL NEOGEO

* * * * *

Manual control:

CLEAR\

SETPWM B1 ORANGE\

SET B2 BLUE\

SET B3 GREEN\

SET B4 RED

* * * * *

Limitations
===========

-   PWM channels are limited
-   not all buttons can have independent shades
-   best results when unused buttons are BLACK

* * * * *

Conclusion
==========

This firmware provides:

-   reliable RGB control
-   smart PWM handling
-   flexible panel system

You can now:

-   connect it to RetroBat or MAME
-   build dynamic lighting systems
-   create custom arcade panels