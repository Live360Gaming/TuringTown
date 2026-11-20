# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

TuringTown is a party game: one **host** screen (a TV, fixed 16:9) and many **player** phones/laptops that join with a
room code. It is a single deployable: an ASP.NET Core 11 server that also serves the built Phaser 3 frontend.

## Commands

Frontend (run from `game/`):
- `npm run dev` : Vite dev server. It has **no API proxy** and the client calls `window.location.origin`, so under
  `vite` alone the `/api/*` calls will not reach the server.
- `npm run build` : outputs straight into the repo-root `wwwroot/` (`outDir: '../wwwroot'`, `emptyOutDir`). `wwwroot/`
  is git-ignored.
- There is no lint script and no frontend tests.

Full stack: `cd game && npm run build`, then from the repo root `dotnet run` (http://localhost:5278, profile `http`).
The server serves `wwwroot/` through `UseDefaultFiles`/`UseStaticFiles`.

Backend (repo root): `dotnet test TuringTown.slnx --configuration Release` is what CI runs. It builds the app and runs
tests, and passes when there are none (there is no test project yet). Single test, once one exists:
`dotnet test --filter "FullyQualifiedName~<TestName>"`.

CI (`.github/workflows/ci.yml`) also builds the Docker image and smoke-tests `/` and `/api/test` with
`scripts/verify-endpoints.sh`. Deploys happen only when a stable GitHub Release is published. See
[docs/RELEASING.md](docs/RELEASING.md).

## Architecture

**Build coupling.** `game/` builds into the ASP.NET `wwwroot/`. The Dockerfile builds the frontend first, then copies
`wwwroot` into the .NET build. Vite uses `base: './'`, so asset URLs in the game must stay **relative** (the game is
served under a sub-path).

**Server** (`Program.cs`, `Controllers/`, `Models/`, `Services/`):
- State is **in memory**: `RoomManager` (singleton) holds a `ConcurrentDictionary<RoomCode, Room>`. Rooms are lost on restart.
- `Room` owns phase, players, settings and the player-limit rules. Controllers are thin and call `Room` methods; the
  server is authoritative for rules (limits come from `GameOptions`, bound from the `Game` section of `appsettings.json`
  and validated on start).
- `POST /api/room/create` returns a room code plus a `HostToken`. Host-only calls send it in the `X-Host-Token` header.
  `POST /api/room/join` adds a player (it never creates a room).
- `GET /api/test` is the health check CI and deploy depend on. Do not remove or rename it.
- `Hubs/` is empty; there is no SignalR yet. Updates are delivered by **polling**.
- Non-GET requests get `no-store` cache headers via middleware in `Program.cs`.

**Client** (`game/src/`):
- One Phaser `Game` (`main.js`) with all scenes registered up front: `Boot` → `Preloader` → `MainMenu` → `Host` or `Player`.
  `HostSettings` and `QuitConfirm` are overlay scenes registered after `Host` so they draw on top.
- Two layouts share one canvas. Boot, menu and **Player** use `Scale.RESIZE` and lay themselves out from the real
  window size. **Host** switches to a fixed 1280x720 (`HOST_*` in `constants.js`) with `FIT`. Switch with
  `enterFullWindow` / `enterFixedSize` in `core/scaling.js`.
- `core/network.js` `NetworkManager` is an event emitter wrapper over `fetch`. Calls return promises **and** emit events,
  and `startPolling(name, path, ms)` emits `poll` events. Host polls `/api/room/<code>` every second.
- Boot loads only the preloader image. Preloader loads all other assets (hard-coded in `Preloader.js`), shows
  `core/ui/LoadingIndicator`, and offers retry when a file fails.
- `core/hostSettings.js` stores the host volume in `localStorage` and applies it as Phaser's global sound volume.

**Content policy.** Anything that produces game content for a room must take a `ContentPolicy` (Family Friendly,
default ON). See [docs/content-policy.md](docs/content-policy.md). The planned design for per-game data and AI-generated
content (schemas, prompts, tone handling) is in [docs/game-data.md](docs/game-data.md). It is design only and is
**not implemented yet**.

## Conventions specific to this repo

- `game/src` files use **CRLF** line endings. Keep them when editing.
- When asked to reorganize or restructure, only **move existing files** (`git mv`) and fix imports. Do not scaffold
  files from a target layout and do not rename files unless asked.
- API testing requests live in `Turing Town Bruno/` (a Bruno collection) and `TuringTown.http`.
