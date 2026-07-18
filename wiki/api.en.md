# Local API

APIExpose exposes a REST API and WebSockets **locally only** (`127.0.0.1`) — nothing leaves your machine.

## The basics

| What | Address |
|---|---|
| **Service status** (API, WebSocket, ES, managers) | `http://127.0.0.1:12345/api/v1/status` |
| Service health | `http://127.0.0.1:12345/api/v1/health` |
| Startup progress | `http://127.0.0.1:12345/api/v1/startup/ready` |
| **Swagger** (all endpoints, testable) | `http://127.0.0.1:12345/swagger/index.html` |
| WebSocket stream discovery | `http://127.0.0.1:12345/api/v1/ws/streams` |
| General WebSocket | `ws://127.0.0.1:12345/ws` |

!!! tip "Swagger is your friend"
    The Swagger UI documents and lets you test every endpoint from the browser, with **pre-filled examples that actually return data** (megadrive/sonic for consoles, mame/1943 for arcade). It is the up-to-date API reference — always in sync with the installed version, and published as a `swagger.json` artifact with every release.

## How the API is organized

Endpoints are grouped with the **same logic as the EmulationStation menu**: each manager is the gateway to its domain, and a manager switched OFF disables its whole branch (see the diagram on the [Menus and options](menus.en.md) page). `GET /api/v1/status` shows the effective state of every gate.

| Swagger group | Domain |
|---|---|
| System & Health | health, startup, service status, effective options |
| Context & Navigation | current game/system, ES control (tap, combo, launch) |
| Local Media Manager | served media, gamelist operations |
| Auto Scraping Manager | scraping state and actions |
| Roms Manager | romset filters, pack installers, gamelists |
| Control Panel Manager | resolved panels, control deployments |
| Themes & Collections | theme exports (.gameinfos) |
| Game Events | scores, arcade outputs, RetroAchievements, hiscores |
| Notifications & UI | toasts, native ES notifications |
| Real-time (WebSocket) | stream discovery |
| Internal & Prototype | maintenance, ingestion, future surfaces |

!!! warning "Experimental endpoints"
    `es/controller/goto-system` and `goto-game` are not yet reliable on every theme/view — prefer `tap`/`combo` sequences. `reloadgames` must never be used as an automatic refresh (the user's cursor is lost).

## The WebSocket streams

Each stream only delivers the event types of its prefixes; some replay their last snapshot on connect (*retained*). The complete list, always in sync with the installed version: `GET /api/v1/ws/streams`. The formal specification (channels, envelope, real examples): [asyncapi.yaml](asyncapi.yaml), also published with every release.

| Stream | Content |
|---|---|
| `/ws/frontend` | ES navigation, game start/end (`ui.*`) |
| `/ws/marquee`, `/ws/topper`, `/ws/instruction-card`, `/ws/screen` | Physical surface media snapshots |
| `/ws/panel` | Resolved panel of the current context (retained `panel.state`) |
| `/ws/ingame` | In-game memory events (RetroArch, MAME Lua) |
| `/ws/arcade` | Native MAME outputs (lamps), sessions |
| `/ws/score`, `/ws/timer` | Normalized live score and timer (retained) |
| `/ws/retroachievements` | RA session: rich presence, unlocks, challenges, leaderboards |
| `/ws/hiscore` | High score captures and updates |
| `/ws/media`, `/ws/roms`, `/ws/system`, `/ws/control`, `/ws/esevent` | Media store, Roms Manager, service, commands, raw ES hooks |

Every event shares one envelope: `{ "Type": "ui.game.selected", "Ts": "...", "NodeId": "...", "CorrelationId": "...", "Payload": { ... } }` — the contract is additive (fields are added, never removed).

## Build your own tool

A browser overlay, a rich presence, a homemade scoreboard: any WebSocket/REST consumer can plug in. Start with:

1. `GET /api/v1/status` to check everything is green;
2. open Swagger and try the endpoints (the examples respond);
3. connect to `ws://127.0.0.1:12345/ws` and watch events while browsing EmulationStation;
4. move to the filtered streams (`/api/v1/ws/streams`) once you know what you need.

!!! note "Stability"
    The streams listed here are the ones MarqueeManager and LedManager consume — they are maintained with priority and evolve in a backward-compatible way.
