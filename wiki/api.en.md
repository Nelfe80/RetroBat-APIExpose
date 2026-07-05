# Local API

APIExpose exposes a REST API and WebSockets **locally only** (`127.0.0.1`) — nothing leaves your machine.

## The basics

| What | Address |
|---|---|
| Service health | `http://127.0.0.1:12345/api/v1/health` |
| Startup progress | `http://127.0.0.1:12345/api/v1/startup/ready` |
| **Swagger** (all endpoints, testable) | `http://127.0.0.1:12345/swagger/index.html` |
| General WebSocket | `ws://127.0.0.1:12345/ws` |

!!! tip "Swagger is your friend"
    The Swagger UI documents and lets you test every endpoint from the browser. It is the up-to-date API reference — always in sync with the installed version.

## Specialized WebSocket streams

Each stream pushes a snapshot on connection, then real-time changes. Reconnections are natural (official plugins retry after 5 seconds).

| Stream | Contents |
|---|---|
| `/ws/marquee` | Marquee/DMD media of the selected game or system |
| `/ws/topper` | Topper media |
| `/ws/instruction-card` | Instruction cards |
| `/ws/frontend` | Game start/end, ES navigation |
| `/ws/arcade` | Native MAME outputs (lamps) and RAM signals |
| `/ws/retroachievements` | RA session: rich presence, unlocks, challenges, leaderboards |
| `/ws/score` | Normalized real-time score, all sources |
| `/ws/timer` | Normalized real-time timer |
| `/ws/hiscore` | High-score notifications |

## Building your own tool

An OBS overlay, a Discord rich presence, a homemade scoreboard: any WebSocket/REST consumer can plug in. Start with:

1. open Swagger to explore the REST endpoints;
2. connect to `ws://127.0.0.1:12345/ws` and watch events while browsing EmulationStation;
3. move to the specialized streams once you know what you are looking for.

!!! note "Stability"
    The streams listed here are the ones MarqueeManager and LedManager consume — they are maintained first and evolve as backward-compatibly as possible.
