# Getting started

Installing APIExpose requires **no installer**: download, extract, activate.

## Before you begin

- a working **RetroBat** installation;
- the **[.NET 8 runtime](https://dotnet.microsoft.com/download/dotnet/8.0)**;
- a backup of your RetroBat folder if your installation matters to you — APIExpose modifies gamelists and settings.

## Installation

1. Download **`APIExpose-x.y.z-full.7z`** from the [releases page](https://github.com/Nelfe80/RetroBat-APIExpose/releases) — it contains the program, the tools (ffmpeg, ImageMagick, translateLocally) and the full Data Pack.
2. Extract the archive into your `RetroBat\plugins\` folder — you get:

    ```text
    RetroBat\plugins\APIExpose\
    ```

3. Close RetroBat if it is running, then double-click **`install-es-start-hook.bat`**. A window confirms the hook installation.
4. Start RetroBat as usual: APIExpose starts automatically, runs its startup work, then lets EmulationStation continue.

!!! note "What does the hook do?"
    It only installs this script on the EmulationStation side, without modifying `updatestores.bat` or anything else in RetroBat:

    ```text
    emulationstation\.emulationstation\scripts\start\APIExpose-start-wait.bat
    ```

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

APIExpose adds its options **to the EmulationStation menus**, translated — no file to edit for everyday use:

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

Files stay in `plugins\APIExpose` — reinstalling the hook brings everything back.

!!! tip "Simple advice"
    Install the hook once, then start RetroBat normally. Do not delete the `media\`, `resources\`, `tools\` or `wrapper\` folders: APIExpose needs them.
