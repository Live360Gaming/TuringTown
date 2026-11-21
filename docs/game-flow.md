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
| Onboarding | Each human answers the questions on their phone | everyone connected has answered, or time runs out | `{questions: [{id, kind, text, mature, options: [{id, text, mature}], minSelect, maxSelect}]}` |
| Discussion | Anonymous chat | timer | none |
| Voting | Each living player votes for another living player | everyone required has voted, or time runs out | none |
| Reveal | Who was voted out, and whether they were a bot | timer | `{eliminated, wasBot, tally}` |
| Results | Winner and every role | host calls `ReturnToLobby`, or timer | `{winner, roles}` |

- **Voting.** Plurality is eliminated. A **tie (or no votes) eliminates nobody.** Eliminated humans stay as read-only
  spectators. Bots vote through the agent and may pick **any living player but themselves** (see
  [What a bot knows](#what-a-bot-knows)).
- **Winning.** Humans win when no bot is left. Bots win when living bots are at least as many as living humans, or when
  `MaxRounds` passes with a bot still alive (so a run of ties cannot stall the game).
- **Bots.** 1 bot for 2-4 humans, 2 for 5 or more (`TuringTownEngine.BotCountFor`). Bots are seated when the game
  starts, with an alias and character from the same pool as humans, and are removed when the room returns to the lobby.
- **Onboarding questions** come in three kinds (`kind`): `Text` (free text), `SingleChoice` (pick exactly one of 2-6
  options, usually four, `minSelect = maxSelect = 1`) and `MultiChoice` (3-6 options, pick `minSelect`..`maxSelect`).
  A game asks a fixed mix, one of each by default (`TextQuestions`, `SingleChoiceQuestions`, `MultiChoiceQuestions`).
  Option ids are `a`..`f`, assigned by the server. There is **never a correct answer**: they are private preference
  questions.
- **Onboarding answers are private.** They are never shown to other players. They go to the bot agent (written text,
  and the option ids the humans picked, with no tokens or player ids) so the bots can match how the humans write and
  what they pick.
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
| `ChatMessage` | `{id, playerId, alias, color, characterId, text, sentAtUtcMs, mentions}` (`mentions` = ids of the players it @mentions) |
| `Progress` | `{done, total}` for the current Onboarding or Voting stage |

Client to server (a refused call throws a `HubException` with a readable message):

| Method | Who | When |
|---|---|---|
| `JoinAsHost(roomCode, hostToken)` / `JoinAsPlayer(roomCode, playerId, playerToken)` | host / player | any time. Returns `{state, roster, you, transcript, serverTimeUtcMs, unmasked}` |
| `StartGame()` | host | Lobby |
| `TutorialStep(step)` / `TutorialDone()` | host | Tutorial |
| `SubmitOnboarding(OnboardingAnswer[] answers)` | player | Onboarding. One `{questionId, text, optionIds}` per question: `text` for a `Text` question, `optionIds` for a choice question. Checked on the server against the questions asked (unknown option, wrong number of picks, text on a choice, over-long or leaky text) |
| `SendChat(text)` | living player | Discussion |
| `CastVote(targetPlayerId)` | living player | Voting. Can be changed until the stage ends |
| `ReturnToLobby()` | host | Results |

`endsAtUtcMs` is the server's clock. Clients compute `serverTimeUtcMs - Date.now()` from the join result and count down
with it, so every screen shows the same time even if a device clock is wrong (`GameSession.msLeft`).

Because the state is a snapshot, a phone that refreshes mid-game just joins again and renders the current stage. The
client keeps the player's credentials in `sessionStorage` for this. The seat stays reserved while disconnected.

## What a bot knows

A bot knows that **it** is a bot, the rules, **how many** bots are in the room (1 or 2), and everything a human seat
sees: the roster by alias, the chat, the Reveal outcomes and tallies, and the humans' onboarding answers as style
samples. It is **never told which other player is human or a bot**, and with two bots neither knows which player is its
teammate. So a bot may accuse or vote for its own teammate without knowing it, and that is intended. Bot requests never
carry an `IsBot` flag for anyone (`BotKnowledgeTests`). Each bot's memory and context are its own: no shared state.

Every request carries a `BotContext` (`Services/Ai/BotContext.cs`): the bot's `BotKey` (a storage key, never put in a
prompt), the bot count, its own persona (its onboarding answers) and latest lines, the other living players by alias, the
finished rounds as the Reveal showed them (`RoundOutcome`, which includes the role of a player who was already voted out,
as every player saw), and a `StyleProfile` of how the humans wrote and what they picked. Free text in the style samples is
untrusted player input: `PromptText.Sanitize` strips invisible and direction-changing characters and caps it, and it must
only ever reach a model as quoted data.

## Chat rules

Players are only ever their alias and character. Enforced in `ChatFilter` and the engine, for humans and bots alike:
length limit, per-player rate limit, living players only, Discussion only, and **no contact details, links or
"my name is..."**. Messages also pass `IContentFilter`, the hook for Azure AI Content Safety (currently allows all).
The server cannot detect an arbitrary real name; that is covered by the bots' instructions and by
`QuestionValidator`, which rejects onboarding questions that ask for a name, age, location, school, workplace or contact.

### @mentions

A player is mentioned by writing `@` and their alias exactly, in any letter case, for example `@Purple Mango`. The
server finds them (`TuringTownEngine.FindMentions`) and puts their ids in the message's `mentions`. Rules:
- Only seated players count, never the sender. Anything else is just text. At most `MaxMentionsPerMessage` (3) count,
  and the longest alias is matched first (so `@Red Apple 2` is not read as `@Red Apple`).
- Humans and bots mention the same way. A bot's reply goes through the same parsing and filters.
- **A bot that is mentioned answers soon.** It is woken from its usual wait and replies after a short pause
  (`BotMentionReplyMinDelayMs`/`MaxDelayMs`) instead of ignoring the question. The lines that mentioned it are passed to
  the agent in `ChatRequest.Mentions`, and `ChatRequest.OtherPlayers` lists everyone else by alias (no roles, so a bot
  can mention anyone, including a teammate it cannot identify).
- Screens: phones show a tap-to-add `@Alias` chip for each other living player, show a message that mentions you in gold,
  and buzz the phone if it can. The TV draws a mentioning message in a golden bubble.

## AI boundary

Two roles, both in `Services/Ai/`, and every call takes the room's `ContentPolicy`:

- `IBotAgent` is a **player**: answer the onboarding form (as text or option ids), say the next chat line, cast a vote.
- `IQuestionGenerator` is **not a player**: it writes the onboarding form (the mix of `Text`, `SingleChoice` and
  `MultiChoice` questions) during the Tutorial. It has no persona and no game context.

- `ScriptedBotAgent` and `BuiltInQuestionGenerator` work offline and are the defaults. They are also the fallbacks.
  The built-in questions (`ScriptedBotAgent.BuiltInQuestions`) include every kind; the scripted bot answers every kind
  validly, picking from what the humans picked.
- `FallbackBotAgent` and `FallbackQuestionGenerator` wrap a real one and answer with a safe fallback on a timeout or
  error, so a game never stalls. Each kind of call has its own timeout (`AgentTimeouts`). A failed **vote** or **onboarding
  answer** is answered by the scripted agent; a failed **chat** turn is **silence** (a canned line in the middle of an AI
  conversation is a tell); a failed end-of-round call is ignored.
- **Memory.** `IBotMemoryStore` (`Services/Storage/`) holds each bot's own notes under its `BotKey`. It has no way to list
  keys, so an agent can only touch its own. It is in memory, also in production (rooms are lost on restart anyway), and the
  engine clears the game's memory when the game ends.
- **End of round.** After each vote the engine calls `IBotAgent.EndRoundAsync` for every living bot, in the background,
  with the round's public chat, so the agent can condense its notes. It never holds up the Reveal.
- **Pacing.** The engine shortens a bot's pause by how long its agent usually takes (so a slow model does not make it
  slower than its pause), adds typing time (`BotTypingMsPerChar`, the agent's own time counts towards it), sometimes
  leaves an @mention for its next normal turn (`BotMentionIgnorePercent`), and with `BotPaceToHumans` never says much
  more than the humans do.
- The engine **validates** generated questions (`QuestionValidator`: the mix, option counts, ids and lengths, no
  personal details in a question or an option, nothing mature in a family room) and every answer, human or bot
  (`OnboardingAnswerValidator`). A bad question set is replaced by the built-in one; a bad bot answer is replaced
  per question.
- **Not written yet:** the real agent and generator (Azure AI Foundry). Their endpoint, auth and request shape are to be
  configuration-bound.

## Configuration

The optional `TuringTown` section of `appsettings.json` (`TuringTownOptions`). Defaults:

| Key | Default | Meaning |
|---|---|---|
| `TutorialMaxSeconds` / `OnboardingSeconds` | 180 / 60 | |
| `DiscussionSeconds` / `VotingSeconds` | 120 / 30 | |
| `RevealSeconds` / `ResultsSeconds` | 8 / 30 | |
| `MaxRounds` | 5 | bots win if one is still alive after this many rounds |
| `TextQuestions` / `SingleChoiceQuestions` / `MultiChoiceQuestions` | 1 / 1 / 1 | onboarding questions of each kind (the form length is their sum) |
| `ChatMaxLength` / `AnswerMaxLength` | 200 / 300 | |
| `ChatMinIntervalMs` | 1500 | per-player chat rate limit |
| `BotMessagesPerRound` | 5 | cost control per bot |
| `MaxMentionsPerMessage` | 3 | players one message may @mention |
| `BotMentionReplyMinDelayMs` / `BotMentionReplyMaxDelayMs` | 1500 / 5000 | how soon a mentioned bot answers |
| `BotChatMinDelayMs` / `BotChatMaxDelayMs` | 4000 / 12000 | bots wait a random time so they type like people |
| `BotTypingMsPerChar` / `BotTypingMaxMs` | 120 / 4000 | typing time before a line appears (the agent's own time counts) |
| `BotMentionIgnorePercent` | 15 | percent of @mentions a bot leaves for its next normal turn |
| `BotPaceToHumans` | false | the bot never says much more than the humans (about 1.3x, at least one line); meant for the real AI |
| `BotOwnLinesContext` | 5 | how many of its own latest lines a bot is shown, so it stays consistent |
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
