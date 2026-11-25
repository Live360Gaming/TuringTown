# Turing Town: game flow

Turing Town is a social-deduction game. One or two **AI bots** hide among the human players. Everyone fills in an
onboarding form, then plays three rounds, each ending in a vote. The first two votes only fill a **suspicion meter**;
**the final vote alone decides the game.**

The **server runs the game** (`Services/Games/TuringTownEngine`, one per game). Clients only render the state it
broadcasts and send their own answers, chat lines and votes. For per-game content and AI generation see
[game-data.md](game-data.md); for the Family Friendly toggle see [content-policy.md](content-policy.md).

Turing Town is one game of a **[game night](game-night.md)**: the room plays it and [Witty Bits](witty-bits.md) in the
order the AI team picks, on the same seats, Humans vs AI on points. Winning Turing Town gives the winning side a flat
bonus. The lobby and the night's own stages (Planning, Scoreboard, Finale) are described there.

## Stages

```
(night) → Tutorial → Onboarding
        → Round 1, Quick Fire: QuickFire → Discussion → Voting → Reveal (meter)
        → Round 2, Hot Seat:                Discussion → Voting → Reveal (meter)
        → Round 3, Final:                   Discussion → Voting → Reveal (verdict) → Results → (night: Scoreboard or Finale)
```

| Stage | What happens | Ends when | `data` |
|---|---|---|---|
| Tutorial | The TV plays the how-to-play. Questions are generated in the background meanwhile. | host calls `TutorialDone` when it ends or is skipped, or the leader skips it from their phone (or the safety limit) | none |
| Onboarding | Each human answers the questions on their phone | everyone connected has answered, or time runs out | `{questions: [{id, kind, text, mature, options: [{id, text, mature}], minSelect, maxSelect}]}` |
| QuickFire | Everyone writes one short answer to the same prompt (`QuickFirePrompts`, family safe). Bots answer too. | everyone connected has answered, or time runs out | `{kind, prompt, hotSeat, meter}` |
| Discussion | Anonymous chat. In Quick Fire it opens with everyone's answers, shuffled. In a Hot Seat the most suspected player is named. Form answers are never shown. | timer | same as QuickFire |
| Voting | Each player votes for another player: who they suspect, or in the Final who is the bot | everyone required has voted, or time runs out | same as QuickFire |
| Reveal | Before the Final: the suspicion meter. In the Final: who was voted out, and whether they were a bot | timer | `{eliminated, wasBot, tally, final, meter}` |
| Results | Winner and every role | host or leader calls `ContinueNight`, or timer | `{winner, roles}` |

- **Rounds.** `MaxRounds` (3) rounds. The last is always the **Final**; round 1 is **Quick Fire** (unless
  `QuickFireSeconds` is 0) and any other is a **Hot Seat**. The hot seat is the top of the meter, a tie settled at random.
- **Suspicion meter.** Each player's share of all votes cast so far (`SuspicionLevel`). It is built only from votes, so
  it says nothing about roles. Nobody is eliminated before the Final. Bots vote through the agent and may pick **any
  player but themselves** (see [What a bot knows](#what-a-bot-knows)).
- **Winning.** The Final vote's plurality is eliminated. A tie goes to whichever of the tied players the meter suspected
  most before the Final. **Humans win if that player is a bot**; otherwise, including no verdict, the bots win. With two
  bots, catching either one is enough.
- **Bots.** The night seats them (`GameNight.BotCountFor`): Turing Town's own rule is 1 bot for 2-4 humans and 2 for 5
  or more (`TuringTownEngine.BotCountFor`), and the night adds enough for four players in all, so 2 humans play with 2
  bots. Bots are seated when the night starts, with an alias and
  character from the same pool as humans, and are removed when the night ends.
- **Onboarding questions** are opinions and hot takes ("is a hot dog a sandwich?", "what is the most overrated food?"), and come in three kinds (`kind`): `Text` (free text), `SingleChoice` (pick exactly one of 2-6
  options, usually four, `minSelect = maxSelect = 1`) and `MultiChoice` (3-6 options, pick `minSelect`..`maxSelect`).
  A game asks a fixed mix, one of each by default (`TextQuestions`, `SingleChoiceQuestions`, `MultiChoiceQuestions`).
  Option ids are `a`..`f`, assigned by the server. There is **never a correct answer**: they are private preference
  questions.
- **Onboarding answers are private for the whole game.** No state, roster or chat message ever carries a player's
  answers, so nobody (TV or phone) sees what anyone else answered (`AnswerPrivacyTests`). Players may of course say their
  own answers in the chat. The humans' answers go to the bot agent **only for the bot's own form** (written text per
  question, and the option ids the humans picked, with no tokens or player ids) so the bots can answer in the room's voice.
  From the first Discussion on, a bot gets only the style numbers (`StyleProfile.WithoutAnswers`), so it can never quote
  or count another player's answers. A bot's text answer that is a near-copy of a human's
  (`OnboardingAnswerValidator.IsNearCopy`) is still replaced, since twins would stand out if both came up in chat.
- **Waiting is bounded.** A player who disconnects is not waited for. A bot whose agent call fails abstains.

## The secret

Whether a player is a bot is the one thing the game depends on. `Player.IsBot` and the engine's role knowledge stay on
the server; clients only get `PlayerInfo {id, alias, color, characterId, alive, connected, ready, leader}`. Roles first appear in
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
| `Typing` | `{playerId, typing}`: a player started or stopped typing in the Discussion. Humans and bots look identical, and the client drops the flag when that player's message arrives, the stage changes, or 30 s pass |
| `Progress` | `{done, total}` for the current Onboarding or Voting stage |
| `GameError` | A generic message when acceptable game content cannot be prepared; the game returns to the lobby |

Client to server (a refused call throws a `HubException` with a readable message):

| Method | Who | When |
|---|---|---|
| `JoinAsHost(roomCode, hostToken)` / `JoinAsPlayer(roomCode, playerId, playerToken)` | host / player | any time. Returns `{state, roster, you, transcript, serverTimeUtcMs, unmasked}` |
| `StartGame()` | host, or the leader player (first human to join) | Lobby. Starts the game night. Refused until `MinPlayers` have joined and all are ready |
| `SetReady(bool ready)` | player | Lobby. Shown on the TV as a green ring and badge |
| `TutorialStep(step)` | host | Tutorial |
| `TutorialDone()` | host, or the leader player (to skip the intro) | Tutorial |
| `SubmitOnboarding(OnboardingAnswer[] answers)` | player | Onboarding. One `{questionId, text, optionIds}` per question: `text` for a `Text` question, `optionIds` for a choice question. Checked on the server against the questions asked (unknown option, wrong number of picks, text on a choice, over-long or leaky text) |
| `SubmitQuickFire(text)` | player | QuickFire. One short answer, same filters as chat; can be changed until the stage ends |
| `SendChat(text)` | living player | Discussion |
| `SetTyping(bool typing)` | living player | Discussion. Sent while the chat box has text, repeated every 4 s, and `false` on send |
| `CastVote(targetPlayerId)` | living player | Voting. Can be changed until the stage ends |
| `ContinueNight()` | host or leader | Results: on to the night's Scoreboard or Finale (`ReturnToLobby()` is the older name) |

`endsAtUtcMs` is the server's clock. Clients compute `serverTimeUtcMs - Date.now()` from the join result and count down
with it, so every screen shows the same time even if a device clock is wrong (`GameSession.msLeft`).

Because the state is a snapshot, a phone that refreshes mid-game just joins again and renders the current stage. The
client keeps the player's credentials in `sessionStorage` for this. The seat stays reserved while disconnected. In the lobby only, a player who is still offline after `Game:LobbyDisconnectGraceSeconds` (default 15) is cleared, their seat is freed and the next player becomes leader. The host TV shows a banner for every drop, return and clear (`PresenceToast`).

## What a bot knows

A bot knows that **it** is a bot, the rules, and everything a human seat sees: the roster by alias, the chat, and the
Reveal outcomes and tallies. It is not told how many bots there are. While answering its own form it also gets the
humans' onboarding answers as anonymous style samples; after that, only style numbers. It is **never told which other
player is human or a bot**, and with two bots neither knows the other exists. So a bot may accuse or vote for the other
bot without knowing it, and that is intended. Bot requests never carry an `IsBot` flag for anyone (`BotKnowledgeTests`).
Each bot's memory and context are its own: no shared state.

**How a bot votes.** It rates every candidate from 0 to 1 on how much they seem like an AI, from what they said (the
`vote-task` prompt: accusing or being accused is not evidence, and neither is writing differently from the bot), and
`VoteChoice` draws the vote from those ratings: weight `0.15 + score²`, so the strongest suspect is the likely pick, never
a sure one, and two bots draw independently. A bot never votes back at whoever accused it: humans do most of the
accusing, so that would pick out the humans. The scripted bot rates by how much the room has accused each player. Check
it in the playground with `/votes` (`scripts/votes-neutral.txt` should land near chance).

Every request carries a `BotContext` (`Services/Ai/BotContext.cs`): the bot's `BotKey` (a storage key, never put in a
prompt), the bot count, its own persona (its onboarding answers) and latest lines, the other living players by alias, the
finished rounds as the Reveal showed them (`RoundOutcome`, which includes the role of a player who was already voted out,
as every player saw), and a `StyleProfile` of how the humans wrote. For the bot's own form (round 0) the profile also holds
what they picked and short samples of what they wrote (with `TextAnswers`, their answers per text question); from the first
Discussion on it is `WithoutAnswers()`, numbers only. Free text in the style samples is
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
- Screens: on phones, typing `@` opens a list of the other living players (filtered by what follows; tap, or
  arrows + Enter/Tab, inserts `@Alias `), show a message that mentions you in gold,
  and buzz the phone if it can. The TV draws a mentioning message in a golden bubble.

## AI boundary

Two roles, both in `Services/Ai/`, and every call takes the room's `ContentPolicy`:

- `IBotAgent` is a **player**: answer the onboarding form (as text or option ids), say the next chat line, cast a vote.
- `IQuestionGenerator` is **not a player**: it writes the onboarding form (the mix of `Text`, `SingleChoice` and
  `MultiChoice` questions) during the Tutorial. It has no persona and no game context.

- `ScriptedBotAgent` and `BuiltInQuestionGenerator` work offline and are the defaults. They are also the fallbacks.
  The built-in questions (`ScriptedBotAgent.BuiltInQuestions`) are opinion questions of every kind; the scripted bot
  answers every kind validly, picking from what the humans picked. It is built not to be predictable: each bot has its
  own `BotVoice` (casing, punctuation, pet fillers, the odd typo or emoji, how chatty it is, leaning towards how the room
  wrote), chooses what to say by intent from large pools (react, answer, accuse with a reason, defend, joke), brings up
  its own form answers (never anyone else's), never repeats one of its recent lines, and votes weighted towards whoever the room (or an
  accuser of its own) has been pointing at.
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
  slower than its pause), adds length-based typing time at a stable per-bot rate with small variation
  (`BotTypingMsPerChar`, capped by `BotTypingMaxMs`; the agent's own time counts towards it), sometimes
  leaves an @mention for its next normal turn (`BotMentionIgnorePercent`), and with `BotPaceToHumans` never says much
  more than the humans do.
- The engine **validates** generated questions (`QuestionValidator`: the mix, option counts, ids and lengths, no
  personal details in a question or an option, nothing mature in a family room) and every answer, human or bot
  (`OnboardingAnswerValidator`). A bad question set is replaced by the built-in one; a bad bot answer is replaced
  per question. Replacements also pass validation and content filtering. The engine searches built-in question alternatives
  for at most two passes and ten seconds; if no acceptable form or bot answer can be prepared, it reports an error and returns
  the room to the lobby.
- **The real agent and generator** (`FoundryBotAgent`, `FoundryQuestionGenerator`) run on Azure AI Foundry when `Ai:Provider=Foundry`. They are
  off by default. Everything about them (configuration, secrets, prompts, limits, abuse protections, testing) is in [ai-agents.md](ai-agents.md).
- At the Results stage the engine can hand the finished game (aliases and game text only) to an opt-in `IGameArchive` (`Storage:ArchiveTranscripts`),
  for tuning the bots. Development enables local JSON files with bot/human labels; other environments default to
  archiving off, and local-file archiving cannot be enabled outside Development. See [ai-agents.md](ai-agents.md#local-training-transcripts).
  No bot ever reads the archive.

## Configuration

The optional `TuringTown` section of `appsettings.json` (`TuringTownOptions`). Defaults:

| Key | Default | Meaning |
|---|---|---|
| `TutorialMaxSeconds` / `OnboardingSeconds` | 180 / 60 | |
| `DiscussionSeconds` / `VotingSeconds` | 120 / 30 | |
| `RevealSeconds` / `ResultsSeconds` | 8 / 30 | |
| `MaxRounds` | 3 | rounds in a game; the last one is the Final |
| `QuickFireSeconds` | 30 | time to write a Quick Fire answer; 0 skips Quick Fire |
| `RoundPoints` | 100 | what round 1 is worth; each round doubles it (100, 200, 400). The humans earn it if the round's top suspect is a bot, otherwise the AI team; a tie earns nobody. Added to the win bonus |
| `TextQuestions` / `SingleChoiceQuestions` / `MultiChoiceQuestions` | 1 / 1 / 1 | onboarding questions of each kind (the form length is their sum) |
| `ChatMaxLength` / `AnswerMaxLength` | 200 / 300 | |
| `ChatMinIntervalMs` | 1500 | per-player chat rate limit |
| `BotMessagesPerRound` | 5 | cost control per bot |
| `MaxMentionsPerMessage` | 3 | players one message may @mention |
| `BotMentionReplyMinDelayMs` / `BotMentionReplyMaxDelayMs` | 1500 / 5000 | how soon a mentioned bot answers |
| `BotChatMinDelayMs` / `BotChatMaxDelayMs` | 4000 / 12000 | bots wait a random time so they type like people |
| `BotTypingMsPerChar` / `BotTypingMaxMs` | 120 / 4000 | typing time before a line appears (the agent's own time counts) |
| `BotMentionIgnorePercent` | 15 | percent of @mentions a bot leaves for its next normal turn |
| `BotPaceToHumans` | unset | the bot never says much more than the humans (about 1.3x, at least one line). Unset means on for a game on the real AI, off for the scripted bots |
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
  `{id, type: 'animation' | 'slide' | 'video', title?, caption, durationMs, narration?, animation?, image?, video?}`. Animations are
  named (`core/tutorial/animations.js`) so the data holds no code. A `video` step ends when the video does. The host reports
  each step with `TutorialStep` so phones show the same title and caption. The leader's phone has a "Skip intro" button.
  `narration` is an announcer line (an audio key) played on the TV only. `durationMs` is then a minimum: the step also waits
  for the line to end plus a short pause, and a missing or broken clip falls back to the visual timing. The music is ducked
  (`MusicPlayer.duck`, a multiplier on the track's own level) from the first line until the tutorial ends or is skipped.
  The mix (voice level, duck depth, fade times, the pause) is `AUDIO_MIX` in `core/music.js`. Turing Town's lines are in
  `public/audio/turing-town/narration/` and are loaded in the background with the stage music (`loadStageMusic`).

### Add a stage or a game

- **New stage:** add it to `Stage` and the engine's flow, then add `<GameId>Host<Stage>` and `<GameId>Player<Stage>` scenes.
- **New game:** see [game-night.md](game-night.md#adding-a-game). Engines share `GameClock` (time and pause),
  `GameStages` (entering a stage, pausing) and implement `IGameEngine`.

## Known limits

- Single instance only (rooms and SignalR groups are in memory).
- A player who quits in the lobby still holds a seat (shown dimmed), which counts toward the minimum.
- The host cannot rejoin a room after a page refresh; the game ends for the room.
- Real names cannot be reliably detected in chat (see Chat rules).
- Some art is still placeholder (tutorial slides).
