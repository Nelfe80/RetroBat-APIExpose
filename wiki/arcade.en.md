# Arcade and panels

APIExpose exposes **arcade data** for cabinets, LEDs and themes: plugins like [LedManager](https://nelfe80.github.io/RetroBat-Led-Manager/) use it to color your buttons, and [MarqueeManager](https://nelfe80.github.io/RetroBat-Marquee-Manager/) to animate lamps and scores.

## Panels (dynpanels)

The `resources\dynpanels\` folder holds control-panel definitions: buttons, colors, control functions, CPO layouts — per system and per game. This is what lets your buttons take the colors of the selected game's real controls.

## RAM definitions

The `resources\ram\` folder holds per-game memory definitions (`.MEM` files): they detect game events in real time — score, lives, power-ups — straight from the game's RAM.

!!! note "The Data Pack"
    `dynpanels`, `ram`, gamelists and the other `resources\` data form the **APIExpose Data Pack**, the result of long curation work. It ships in the `full` release archive and is protected by its own license (`DATA-LICENSE.md`) — see [Licensing](licences.md).

## The RetroArch wrapper

To read game RAM, APIExpose relies on `wrapper\wrapper.dll`, a libretro proxy DLL that sits between RetroArch and the emulation core, without modifying RetroArch.

Every published version ships with its fingerprint in `wrapper\WRAPPER_VERSION.txt`:

```powershell
Get-FileHash wrapper\wrapper.dll -Algorithm SHA256
```

The hash must match the one in `WRAPPER_VERSION.txt` and in the release notes. If it differs, do not use the DLL.

## High scores and MAME outputs

APIExpose also exposes:

- arcade **high scores** (via hi2txt);
- **native MAME outputs** (`READY_LAMP`, `TORP_LAMP_1`…) on the `/ws/arcade` stream, so lamps and LEDs live again like on the original cabinet;
- the current game context and runtime events, for overlays, themes or external tools.

Real-time streams are detailed in [Local API](api.md).
