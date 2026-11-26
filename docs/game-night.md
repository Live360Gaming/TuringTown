# Game night

A room plays a **game night**: several mini games in a row, **Humans vs AI**. When the host (or the leader) presses
Start, the AI team chooses the order of the games, the same seats play every game, and at the end the side with more
points wins the night. There are three games, [Turing Town](game-flow.md), [Witty Bits](witty-bits.md) and
[Shout Out](shout-out.md) (buzz-in trivia); the night
plays every **enabled** one and the AI only chooses the order.

**Turning a game off.** Each game's `game/src/minigames/<id>/data/game.json` has an `"enabled"` flag (true when missing).
The server embeds these files (`GameDefinitions`) and leaves a disabled game out of the night. **Witty Bits is disabled for
now**, so a night is Turing Town and Shout Out in the order the AI picks. Set the flag to `true` to bring it back; a rebuild is
needed, since the files are compiled into the server.

The server runs the night (`Services/Games/GameNight`, one per room, started by `GameCoordinator`). Each game is an
`IGameEngine`; the night plays them one after another and keeps the score.

## Stages

```
Lobby → Planning → game 1 → Scoreboard → game 2 → … → Finale → Lobby
```

The night's own stages carry the game id `game-night`; a game's stages carry its own id (`turing-town`, `witty-bits`),
so the client finds every screen by name (`<GameId><Role><Stage>`, see `core/director.js`).

| Stage | What happens | Ends when | `data` |
|---|---|---|---|
| Planning | The AI team picks the order, then the TV shows it with a short line from the AI | the plan has been shown (`PlanRevealSeconds`) | `{order: [{gameId, title}], line}` (`order` is empty while the AI is choosing) |
| *a game* | The game's own stages, ending with its Results | the host or leader continues past the Results | the game's own |
| Scoreboard | Everyone's points so far, what is next, and the team totals once they are public | host or leader calls `ContinueNight`, or timer | `{played, next, standings: [{playerId, alias, points}], teams}` |
| Finale | Humans vs AI totals, the winner (`Humans`, `AI` or `Tie`) and every role | host or leader calls `ContinueNight`, or timer | `{winner, teams: {humans, ai}, standings, roles}` |

A game that fails (for example no acceptable content could be prepared) or a closed room ends the night: the room goes
back to the lobby. The bots leave only then.

## Seats

`GameNight.BotCountFor`: enough bots for Turing Town (1 for up to 4 humans, 2 for more) **and** at least four players in
all, which Witty Bits needs. So 2 humans get 2 bots, 3 or 4 get 1, and 5 or more get 2. The same seats (alias and
character) play every game of the night, and everyone is back in play at the start of each game (`Room.ReviveAll`).

## Score

- **A team game** (Turing Town) gives its winning side a flat bonus, `GameNight:TuringTownWinPoints` (1000).
- Turing Town also pays **per round**, doubling each round (`TuringTown:RoundPoints`: 100 for the first vote in round 1,
  400 for the final vote in round 3; the hot seat round has no vote): the humans earn the round's value when its top suspect is a bot, the AI team when it is a human (`GameOutcome.TeamPoints`).
- **A points game** (Witty Bits) gives each team the **average** points of its seats, so a table of four humans does not
  beat one bot just by having more players.
- The Finale names the side with the higher total.

## The secret across games

Who is an AI must stay hidden until Turing Town reveals it, even when Witty Bits comes first. So:

- Witty Bits shows points per player by alias, like any player, and never says which answers came from the AI.
- The **team totals are hidden** (`teams` is null) on the Scoreboard until every bot has been unmasked. A total that
  matches one player's points would give that bot away. In practice they appear once Turing Town has been played, and
  always at the Finale.
- The join result's `unmasked` list covers the whole night, so a refreshed screen keeps drawing revealed bots as robots
  in the next game.

## The AI team

- **Planner** (`IGameNightPlanner`): the AI team's coach, not a player. It gets the games (id, title, description), the
  aliases and the bot count, and returns the order and a line for the TV. The night checks that the order plays every
  game exactly once, and uses a random order if the plan is missing, slow (`GameNight:PlanningMaxSeconds`) or invalid.
  The line is sanitised and dropped if it gives the AI away. Offline it is `ScriptedGameNightPlanner`; on the real AI,
  `FoundryGameNightPlanner` (see [ai-agents.md](ai-agents.md)).
- **One side on the scoreboard, but each bot plays alone.** No bot is told how many bots there are or that it has a team
  (see [ai-agents.md](ai-agents.md)). Each game seats fresh agents (a bot's per-game memory is cleared at the end of every
  game), but every bot request in every game carries the night's `TeamContext` (`NightContext.Snapshot()`): the goal (earn
  as many points as you can), the games and which one is on, the standings, the team totals **only once they are
  public**, and highlights the whole room saw (Turing Town reveals, the answers that won Witty Bits matchups). It never says
  which seat is a bot, so inside Turing Town a bot still does not know who its teammate is (`BotKnowledgeTests`).
- The real AI or the scripted bots are chosen **once per night** (`AgentSelector`), so a budget running out never changes
  the bots mid-night.

## Hub calls

| Method | Who | When |
|---|---|---|
| `ContinueNight()` | host or leader | a game's Results, the Scoreboard, or the Finale (back to the lobby) |
| `ReturnToLobby()` | host or leader | the older name of `ContinueNight`, kept for old clients |

Pausing works inside a game only; the night's own stages are short.

## Configuration

The optional `GameNight` section (`GameNightOptions`):

| Key | Default | Meaning |
|---|---|---|
| `PlanningMaxSeconds` | 15 | the longest the AI may take to plan; then the order is random |
| `PlanRevealSeconds` | 6 | how long the TV shows the order |
| `ScoreboardSeconds` / `FinaleSeconds` | 20 / 45 | |
| `TuringTownWinPoints` | 1000 | the bonus for winning Turing Town |

## Adding a game

1. Write an `IGameEngine` (see `WittyBitsEngine` for a small one; `GameStages` and `GameClock` do the stage and pause
   plumbing). Its `PlayAsync` ends when its Results stage is over and returns a `GameOutcome`: a `Winner` for a team game,
   or `SeatPoints` for a points game. It never resets the room.
2. Give it a `data/game.json` with `"id"` and `"enabled"`. Add a `GameIds` constant, any new `Stage` values and data records (no role in any of them), and a `NightGameEntry` in
   `GameCoordinator.Catalog` with a description the planner can read.
3. Add `minigames/<id>/` on the client: `index.js` with the scenes (`<GameId>Host<Stage>`, `<GameId>Player<Stage>`),
   `music.js` (`trackFor`, registered in `minigames/stageMusic.js`) and `data/`. Spread the scenes in `main.js`.
4. If the game needs more seats, raise `GameNight.BotCountFor`. Pass the `NightContext` to its bots.
