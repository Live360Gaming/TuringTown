# Turing Town: game flow

Turing Town is a social-deduction game. One or two **AI bots** hide among the human players. Everyone fills in an
onboarding form, chats anonymously for a timed discussion, then votes someone out. **Humans win by voting out every
bot. The bots win by surviving until they match the humans.**

The **server runs the game** (`Services/Games/TuringTownEngine`, one per room). Clients only render the state it
broadcasts and send their own answers, chat lines and votes. For per-game content and AI generation see
[game-data.md](game-data.md); for the Family Friendly toggle see [content-policy.md](content-policy.md).

## Stages

```
Lobby → Tutorial → Onboarding → Discussion → Voting → Reveal ─┬─ no winner → Discussion (next round)
                                                              └─ winner    → Results → Lobby
```

| Stage | What happens | Ends when | `data` |
|---|---|---|---|
| Lobby | Players join; host changes settings | host calls `StartGame` (needs `MinPlayers`) | none |
| Tutorial | The TV plays the how-to-play. Questions are generated in the background meanwhile. | host calls `TutorialDone` (or the safety limit) | none |
| Onboarding | Each human answers the questions on their phone | everyone connected has answered, or time runs out | `{questions: [{id, text, mature}]}` |
| Discussion | Anonymous chat | timer | none |
| Voting | Each living player votes for another living player | everyone required has voted, or time runs out | none |
| Reveal | Who was voted out, and whether they were a bot | timer | `{eliminated, wasBot, tally}` |
| Results | Winner and every role | host calls `ReturnToLobby`, or timer | `{winner, roles}` |

- **Voting.** Plurality is eliminated. A **tie (or no votes) eliminates nobody.** Eliminated humans stay as read-only
  spectators. Bots vote through the agent and only pick among humans.
- **Winning.** Humans win when no bot is left. Bots win when living bots are at least as many as living humans, or when
  `MaxRounds` passes with a bot still alive (so a run of ties cannot stall the game).
- **Bots.** 1 bot for 2-4 humans, 2 for 5 or more (`TuringTownEngine.BotCountFor`). Bots are seated when the game
  starts, with an alias and character from the same pool as humans, and are removed when the room returns to the lobby.
- **Onboarding answers are private.** They are never shown to other players. They go to the bot agent (text only, no
  tokens or ids) so the bots can match how the humans write.
- **Waiting is bounded.** A player who disconnects is not waited for. A bot whose agent call fails abstains.

## The secret

Whether a player is a bot is the one thing the game depends on. `Player.IsBot` and the engine's role knowledge stay on
the server; clients only get `PlayerInfo {id, alias, color, characterId, alive, connected}`. Roles first appear in
`Reveal` (the eliminated player) and `Results` (everyone). The roster is sorted by alias so seat order is no tell.
`SecrecyAndIdentityTests` serialises every broadcast and fails if a role or token appears early. **Never add a role,
token or "is bot" flag to a DTO, a log line sent to clients, or a chat message.**

**How a bot looks.** Bots are given a human-looking character like everyone else (the server only hands out the six
`character_NN` sprites, never the robots), so appearance is no tell. The robot art (`robot_01..03`) is drawn only after
a bot is unmasked: when the Reveal stage says `wasBot`, and for everyone at Results. The one exception to "no role
before the reveal" is the join result's `unmasked` list, the ids of bots whose reveal has already been announced, so a
refreshed screen keeps showing them as robots. `TuringTownEngine` only adds a bot to it once its Reveal stage begins.
There are 6 human sprites for up to 8 seats (6 humans + 2 bots), so a face can repeat in a full room; aliases still differ.

## Hub contract (`/hubs/game`)

One SignalR group per room code. Clients authenticate with the tokens from `POST /api/room/create` and `/join`.

Server to client:

| Event | Payload |
|---|---|
| `StateChanged` | `{gameId, stage, round, step, endsAtUtcMs, data}`. Always a **full snapshot**, never a delta. |
| `RosterChanged` | `PlayerInfo[]` (also changes when someone connects, disconnects or is voted out) |
| `ChatMessage` | `{id, playerId, alias, color, characterId, text, sentAtUtcMs}` |
| `Progress` | `{done, total}` for the current Onboarding or Voting stage |

Client to server (a refused call throws a `HubException` with a readable message):

| Method | Who | When |
|---|---|---|
| `JoinAsHost(roomCode, hostToken)` / `JoinAsPlayer(roomCode, playerId, playerToken)` | host / player | any time. Returns `{state, roster, you, transcript, serverTimeUtcMs, unmasked}` |
| `StartGame()` | host | Lobby |
| `TutorialStep(step)` / `TutorialDone()` | host | Tutorial |
| `SubmitOnboarding(string[] answers)` | player | Onboarding (one answer per question) |
| `SendChat(text)` | living player | Discussion |
| `CastVote(targetPlayerId)` | living player | Voting. Can be changed until the stage ends |
| `ReturnToLobby()` | host | Results |

`endsAtUtcMs` is the server's clock. Clients compute `serverTimeUtcMs - Date.now()` from the join result and count down
with it, so every screen shows the same time even if a device clock is wrong (`GameSession.msLeft`).

Because the state is a snapshot, a phone that refreshes mid-game just joins again and renders the current stage. The
client keeps the player's credentials in `sessionStorage` for this. The seat stays reserved while disconnected.

## Chat rules

Players are only ever their alias and character. Enforced in `ChatFilter` and the engine, for humans and bots alike:
length limit, per-player rate limit, living players only, Discussion only, and **no contact details, links or
"my name is..."**. Messages also pass `IContentFilter`, the hook for Azure AI Content Safety (currently allows all).
The server cannot detect an arbitrary real name; that is covered by the bots' instructions and by
`QuestionValidator`, which rejects onboarding questions that ask for a name, age, location, school, workplace or contact.

## AI boundary

Everything the AI does goes through `IBotAgent` (`Services/Ai/`): generate the onboarding questions, answer them as a
bot, say the next chat line, cast a vote. Every call takes the room's `ContentPolicy`.

- `ScriptedBotAgent` works offline and is the default. It is also the fallback.
- `FallbackBotAgent` wraps a real agent and answers with the fallback on a timeout or error, so a game never stalls.
- **Not written yet:** the real agent (Azure AI Foundry). Its endpoint, auth and request shape are unknown, so it is to
  be configuration-bound. Questions it returns are validated and replaced by built-in ones if invalid.

## Configuration

The optional `TuringTown` section of `appsettings.json` (`TuringTownOptions`). Defaults:

| Key | Default | Meaning |
|---|---|---|
| `TutorialMaxSeconds` / `OnboardingSeconds` | 180 / 60 | |
| `DiscussionSeconds` / `VotingSeconds` | 120 / 30 | |
| `RevealSeconds` / `ResultsSeconds` | 8 / 30 | |
| `MaxRounds` | 5 | bots win if one is still alive after this many rounds |
| `QuestionCount` | 3 | onboarding questions |
| `ChatMaxLength` / `AnswerMaxLength` | 200 / 300 | |
| `ChatMinIntervalMs` | 1500 | per-player chat rate limit |
| `BotMessagesPerRound` | 5 | cost control per bot |
| `BotChatMinDelayMs` / `BotChatMaxDelayMs` | 4000 / 12000 | bots wait a random time so they type like people |
| `BotVoteMinDelayMs` / `BotVoteMaxDelayMs` | 3000 / 15000 | |

The player limits (`Game:MaxPlayers`, `Game:MinPlayers`) are separate and belong to `GameOptions`. `MaxPlayers` is **4**
for now: 4 humans plus 1 bot is 5 seats, which fits the 6 human sprites with no repeated face. Raising it to 5 or 6
brings the second bot (`BotCountFor`) and up to 8 seats, so add more human sprites first (`Models/Characters.cs`,
`constants.js` and the Preloader list them).

## Client

- `core/session.js` `GameSession`: owns the connection and everything the server sent. Scenes read it and listen to its
  events (`state`, `roster`, `chat`, `progress`), and never create or stop the connection.
- `core/director.js`: on each `state`, starts the scene named `<GameId><Role><Stage>`, e.g. `TuringTownPlayerVoting`. It
  returns to `Host` / `PlayerLobby` on Lobby. There is no registry: the game exports its scenes and `main.js` spreads them in.
- `core/stage/HostStage.js` and `PlayerStage.js`: base screens (fixed 16:9 TV, and the phone card). Subclasses implement `build()`
  and, on phones, `layout(m)`.
- `core/tutorial/TutorialPlayer.js`: plays an ordered list of steps on the TV. A step is plain data:
  `{id, type: 'animation' | 'slide' | 'video', caption, durationMs, narration?, animation?, image?, video?}`. Animations are
  named (`core/tutorial/animations.js`) so the data holds no code. A `video` step ends when the video does. The host reports
  each step with `TutorialStep` so phones show the same caption.

### Add a stage or a game

- **New stage:** add it to `Stage` and the engine's flow, then add `<GameId>Host<Stage>` and `<GameId>Player<Stage>` scenes.
- **New game:** add a `minigames/<id>/` folder with the same scenes and a `data/` folder (see [game-data.md](game-data.md)).
  The engine is Turing Town specific today; extract the shared parts (timers, progress, chat) when a second game exists.

## Known limits

- Single instance only (rooms and SignalR groups are in memory).
- A player who quits in the lobby still holds a seat (shown dimmed), which counts toward the minimum.
- The host cannot rejoin a room after a page refresh; the game ends for the room.
- Real names cannot be reliably detected in chat (see Chat rules).
- Art, narration and the how-to-play are placeholders.
