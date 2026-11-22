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

Backend (repo root): `dotnet test TuringTown.slnx --configuration Release` is what CI runs. Tests live in
`TuringTown.Tests/` (xUnit). Game rules run on a fake clock (`Harness.cs`), and `HubIntegrationTests` plays a whole
game over real SignalR connections. Single test: `dotnet test TuringTown.Tests --filter "FullyQualifiedName~<TestName>"`.
The root project's globbing excludes `TuringTown.Tests/**` (see `TuringTown.csproj`); keep that when adding files there.
`TestEnvironment.cs` forces `Ai:Provider=Scripted`, archiving off and content safety off through environment variables, so
a developer's user secrets never make a test call Azure. A test that needs another setting overrides it in code.
`BotEvalTests` (the real model) is skipped unless `TURINGTOWN_EVAL=1` and the Foundry settings are set
(`TURINGTOWN_EVAL_GAMES` sets how many games it plays, 3 by default).

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
- `Hubs/GameHub.cs` is the SignalR hub at `/hubs/game` (one group per room code). `POST /api/room/join` returns
  `roomCode`, `playerId`, `playerToken`, and the random `alias` ("Purple Mango"), `color` and `characterId`; the client
  then calls `JoinAsPlayer`. The host calls `JoinAsHost` with its `HostToken`. Joins return the current state, roster
  and chat so a late or refreshed client can render at once. The lobby player count and settings are still **polled**
  (`GET /api/room/<code>`).
- **The game night.** `StartGame` starts a `Services/Games/GameNight` (one per room, started by `GameCoordinator`):
  Planning (the AI team picks the order of the games) → each game → Scoreboard → ... → Finale → Lobby, Humans vs AI on
  points, on the same seats (`GameNight.BotCountFor`). Each game is an `IGameEngine` that returns a `GameOutcome` and never
  resets the room; the night keeps the score and hides the team totals until every bot is unmasked. Details in
  [docs/game-night.md](docs/game-night.md).
- **The games.** Turing Town (a social-deduction game: AI bots hide among humans, then chat and vote) is run by
  `Services/Games/TuringTownEngine`. It walks the stages
  Tutorial → Onboarding → Discussion → Voting → Reveal (repeat) → Results and broadcasts a full `GameState` on every
  change. Witty Bits (prompts, funny answers, head-to-head votes) is `Services/Games/WittyBitsEngine`, see
  [docs/witty-bits.md](docs/witty-bits.md). Both share `GameClock` and `GameStages`. **Roles (`Player.IsBot`) never leave the server before the reveal**: clients only get `PlayerInfo`, and
  `SecrecyAndIdentityTests` guards this. Do not add a role or token to any DTO. Bots use `IBotAgent`
  (`Services/Ai/`); `ScriptedBotAgent` works offline and is the fallback when the real AI is not available.
  Timings are the `TuringTown`, `WittyBits` and `GameNight` config sections (all optional). Full details, hub contract
  and win rules are in [docs/game-flow.md](docs/game-flow.md). The bots of a night are one team: every bot request carries
  the night's public `TeamContext` (`NightContext`), which never names a bot.
- Non-GET requests get `no-store` cache headers via middleware in `Program.cs`.
- Room create/join are rate limited per client address (`RateLimits:RoomRequestsPerMinute`, the `rooms` policy).
- `Services/Storage/`: `IBotMemoryStore` is per-bot memory keyed by `BotKey` (one game only, no way to list keys, never
  holds roles). `IGameArchive` saves transcripts only when `Storage:ArchiveTranscripts` is on (in memory, or Azure Blob).
- Seasonal art: `SeasonProvider` (`Season:Mode`, Auto = Christmas in December) is served by `GET /api/config`.
  `core/season.js` loads it before the art, and a season's `assets/seasons/<season>/manifest.json` lists only the
  texture keys it replaces; everything else keeps the normal art.

**Client** (`game/src/`):
- One Phaser `Game` (`main.js`) with all scenes registered up front: `Boot` → `Preloader` → `MainMenu` → `Host` (the TV
  lobby), or `Player` (join) → `PlayerLobby`. The game's own screens, one per stage and role, come from
  `minigames/<game>/index.js` (`turing-town`, `witty-bits`, and `game-night` for Planning/Scoreboard/Finale). `HostSettings` and
  `QuitConfirm` are overlay scenes registered after `Host` so they draw on top.
- `core/session.js` `GameSession` owns the hub connection, roster, state and chat, and lives on `game.registry`. Scenes
  read and listen to it; they **never create or stop the connection** (only `leaveToMenu` does). `core/director.js`
  starts the scene for each stage the server announces, found **by name**, `<GameId><Role><Stage>` (for example
  `TuringTownHostDiscussion`), so there is no scene registry. After a game night the director brings everyone back to
  `Host`/`PlayerLobby`, which reuse the same session. A player's credentials are kept in `sessionStorage` so a page
  refresh rejoins the same seat.
- Game screens extend `core/stage/HostStage.js` (TV, fixed 16:9) or `core/stage/PlayerStage.js` (phone card layout) and
  implement `build()` / `layout(m)`. The TV how-to-play is a data-driven step list played by `core/tutorial/TutorialPlayer.js`
  (animation / slide / video steps; see `minigames/turing-town/data/tutorial.js`).
- Character art is real: six human sprites (`character_01..06`) and three robots (`robot_01..03`) in
  `game/public/assets/characters/` (512x512 transparent PNGs; keys in `constants.js`). **Robots are never assigned as a
  player's character**: the server only hands out the human sprites, so a bot looks like any player. The client draws a
  robot only after a bot is unmasked (`GameSession.spriteKey`: the Reveal stage's `wasBot`, the Results roles, or the
  join result's `unmasked` list for a refreshed client). Always draw avatars through `createAvatar` so this holds.
  Other placeholder images (tutorial slides) come from `core/placeholders.js`, which steps aside for real art.
- Two layouts share one canvas. Boot, menu and **Player** use `Scale.RESIZE` and lay themselves out from the real
  window size. **Host** switches to a fixed 1280x720 (`HOST_*` in `constants.js`) with `FIT`. Switch with
  `enterFullWindow` / `enterFixedSize` in `core/scaling.js`.
- `core/network.js` `NetworkManager` is an event emitter wrapper over `fetch`. Calls return promises **and** emit events,
  and `startPolling(name, path, ms)` emits `poll` events. Host polls `/api/room/<code>` every second.
- Boot loads only the preloader image. Preloader loads all other assets (hard-coded in `Preloader.js`), shows
  `core/ui/LoadingIndicator`, and offers retry when a file fails.
- Look and feel: `core/theme.js` holds the colors, font stacks and text styles (`textStyle`, `readableOn`); the same values are
  `--tt-*` variables in `game/public/style.css`, which also styles the HTML form inputs. Fonts (Bungee, Nunito Sans, Space
  Mono; SIL OFL) are self-hosted in `game/public/fonts/` and waited for by the Preloader. `installTextDefaults()` (called in
  `main.js`) makes every `add.text` use the UI font. Player names on the TV use `core/ui/Nameplate.js`.
- `core/hostSettings.js` stores the host volume in `localStorage` and applies it as Phaser's global sound volume.
- Music is **TV only**: `core/music.js` (`getMusic(game)`, one crossfading player per game, not tied to a scene) plays
  the track each game's `music.js` picks for its stages (`trackFor`, looked up by `gameId` in `minigames/stageMusic.js`). The host session follows the stage
  (`GameSession.attachDirector`); phones never play music. The Preloader loads only the lobby track, and `Host` loads
  the rest in the background. Files are in `game/public/audio/turing-town/music/`.

**Content policy.** Anything that produces game content for a room must take a `ContentPolicy` (Family Friendly,
default ON). See [docs/content-policy.md](docs/content-policy.md). The design for per-game data and AI-generated
content (schemas, prompts, tone handling) is in [docs/game-data.md](docs/game-data.md). The bots go through `IBotAgent` and the
onboarding questions through `IQuestionGenerator`. Both run **offline by default** (`Ai:Provider=Scripted`), and on Azure AI Foundry
when `Ai:Provider=Foundry`, with the scripted agents as the fallback so a game never stalls. How it works, the config and secrets
(`Foundry:Endpoint` / `Deployment` / `ApiKey` are the existing user secrets; managed identity in production), the abuse protections
and the evaluation tests are in [docs/ai-agents.md](docs/ai-agents.md). `AllowAllContentFilter` is still the default
`IContentFilter`; `ContentSafety:Provider=Azure` switches on Azure AI Content Safety.

**Hosting limit.** Rooms, engines and SignalR groups are all in one process's memory, so the app must run as a
**single instance**. Scaling out would need a SignalR backplane and shared room state.

## Conventions specific to this repo

- `game/src` files use **CRLF** line endings. Keep them when editing.
- When asked to reorganize or restructure, only **move existing files** (`git mv`) and fix imports. Do not scaffold
  files from a target layout and do not rename files unless asked.
- API testing requests live in `Turing Town Bruno/` (a Bruno collection) and `TuringTown.http`.
