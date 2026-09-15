# Getting started

Installing APIExpose is a single **installer**: download, run, activate.

## Before you begin

- a working **RetroBat** installation;
- an Internet connection if **.NET 8** is not installed yet: the installer downloads and installs whatever is missing (ASP.NET Core Runtime 8 and .NET Desktop Runtime 8) on its own, Windows simply asks for your permission;
- a backup of your RetroBat folder if your installation matters to you - APIExpose modifies gamelists and settings.

## Installation

1. Download **[`APIExpose-Cabinet-Setup.exe`](https://github.com/Nelfe80/RetroBat-APIExpose/releases/latest/download/APIExpose-Cabinet-Setup.exe)** from the releases page - it contains the program, the tools (ImageMagick, translateLocally) and the full Data Pack.
2. Run the installer: it checks .NET 8, installs the plugin into `RetroBat\plugins\` and places the EmulationStation start hook - you get:

    ```text
    RetroBat\plugins\APIExpose\
    ```

3. Start RetroBat as usual: APIExpose starts automatically, runs its startup work, then lets EmulationStation continue.

!!! note "What does the hook do?"
    The installer only registers this script on the EmulationStation side, without modifying `updatestores.bat` or anything else in RetroBat:

    ```text
    emulationstation\.emulationstation\scripts\start\APIExpose-start-wait.bat
    ```

    It makes EmulationStation wait until APIExpose is ready, two minutes at most, and not at all if APIExpose cannot start: RetroBat never stays stuck. What happened at the last startup is written to `plugins\APIExpose\.log\es-start-hook.log`.

## Check that it works

With APIExpose running, open in a browser:

```text
http://127.0.0.1:12345/api/v1/health
```

Expected answer:

```json
{ "status": "healthy", "version": "1.0.0+..." }
```

Startup progress lives at `/api/v1/startup/ready`, and the full endpoint list at `http://127.0.0.1:12345/swagger/index.html`.

## Your settings, right inside RetroBat

APIExpose adds its options **to the EmulationStation menus**, translated - no file to edit for everyday use:

```text
EXTENDED OPTIONS
API SETTINGS
AUTO SCRAPING MANAGER
LOCAL MEDIA MANAGER
ROMS PACK MANAGER
THEMES MANAGER
COLLECTIONS PACK MANAGER
```

## Stop or uninstall

| Action | How |
|---|---|
| Stop APIExpose | Double-click `stop.bat` |
| Remove the automatic startup | Double-click `uninstall-es-start-hook.bat` |
| Uninstall everything | Windows Settings, Apps, "APIExpose (borne RetroBat)": the hook is removed too |

Files stay in `plugins\APIExpose` - reinstalling the hook brings everything back.

!!! tip "Simple advice"
    Install the hook once, then start RetroBat normally. Do not delete the `media\`, `resources\`, `tools\` or `wrapper\` folders: APIExpose needs them.
