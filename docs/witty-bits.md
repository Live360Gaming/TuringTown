# Witty Bits

> **Disabled for now** (`"enabled": false` in `game/src/minigames/witty-bits/data/game.json`): nights do not play it
> until the flag is set back to `true`. See [game-night.md](game-night.md).

A comedy-writing game played as part of the [game night](game-night.md). Everyone, humans and the hidden AI players
alike, writes short funny answers to prompts. The answers to a prompt go head to head with no names shown, and everyone
who did not write them votes for the funnier one. Points go to each player, and through them to their team.

The server runs it (`Services/Games/WittyBitsEngine`); the client screens are in `game/src/minigames/witty-bits/`.

## Stages

```
Tutorial → Writing (round 1) → Matchup ⇄ MatchupResult (one per prompt) → Writing (round 2) → … → Writing (Last Bit) → Matchup → MatchupResult → Results
```

| Stage | What happens | Ends when | `data` |
|---|---|---|---|
| Tutorial | The TV plays the how-to-play while the prompts are written | host calls `TutorialDone` or the leader skips (or the safety limit) | none |
| Writing | Each player answers their prompts on their phone | everyone connected (and every bot) has answered, or time runs out | `{round, lastBit, assignments: [{promptId, text, playerIds}]}` (never the answers) |
| Matchup | One prompt's answers, with no authors; everyone else votes | everyone required has voted, or time runs out | `{round, lastBit, index, count, prompt, answers: [{id, text, missing}], authors}` |
| MatchupResult | Who wrote what, the votes and the points | timer | `{round, lastBit, index, count, prompt, answers: [{id, text, missing, authorId, alias, votes, points, sweep}]}` |
| Results | Everyone's points this game | host or leader calls `ContinueNight`, or timer | `{standings: [{playerId, alias, points}]}` |

## Rules

- **Pairing.** With N players (at least 4) there are N prompts per paired round. The seats are shuffled once; in round
  *r* prompt *i* goes to seat *i* and seat *i + r*, so everyone answers two prompts a round and faces different players.
- **Paired rounds** (`PairRounds`, 2). Answers are shown as `a`, `b` in a random order. The two authors sit the vote out.
  Points: `MatchupPoints × round × votes / total votes` (1000 split by vote share in round 1, doubled in round 2), plus
  `SweepBonus × round` for an answer that gets every vote.
- **A missing answer** shows as "No answer" and cannot be voted for. If only one of the two answered, there is no vote:
  that answer wins `ForfeitPoints × round`. If neither did, the prompt is skipped.
- **The Last Bit.** Everyone answers one prompt. Only written answers are shown; everyone votes, but never for their own
  answer. Each vote is worth `LastBitPointsPerVote` (400).
- Votes can be changed until the vote ends. A player who disconnects is not waited for.
- Answers are checked like chat (`ChatFilter`: length, no contact details or links, no "my name is"), then
  `IContentFilter`, with the room's `ContentPolicy`.

## Bots

Bots play exactly like humans and are never labelled: an answer gets its author's alias only after the vote, and nothing
in Witty Bits says whether that alias is a bot (`GetUnmasked` is always empty).

- `IWittyBotAgent` writes an answer and votes. A bot writes at a random moment within the stage
  (`BotAnswerMinDelayMs`..`BotAnswerMaxDelayMs`) and votes after a short pause. It is never told who wrote an answer, and
  never offered its own Last Bit answer.
- Its answers pass the same checks as a human's. A bad one is replaced by a scripted answer, which is checked too. A bot
  that was too slow still answers (with a scripted line), so a missing answer is only ever a human's.
- A null or unknown vote is an abstention.
- Every request carries the night's `TeamContext` (see [game-night.md](game-night.md)), so the AI can play to what this
  room has found funny.
- Offline: `ScriptedWittyBotAgent` (canned answers per built-in prompt, a random vote). On the real AI:
  `FoundryWittyBotAgent` wrapped in `FallbackWittyBotAgent` (see [ai-agents.md](ai-agents.md)).

## Prompts

`IWittyPromptGenerator` writes `PairRounds × N + 1` prompts during the tutorial. `WittyPromptValidator` checks them (the
count, 8-140 characters, no duplicates, no personal details, nothing mature in a family room) and every prompt passes
`IContentFilter`; otherwise the built-in prompts (`BuiltInWittyPrompts`, with a few answers each for the scripted bots)
are used. If not even those pass the content filter, the game reports `GameError` and the night ends.

## Hub calls

| Method | Who | When |
|---|---|---|
| `SubmitWittyAnswer(promptId, text)` | player | Writing, for one of your own prompts. Can be rewritten until the stage ends |
| `CastWittyVote(answerId)` | player | Matchup (`a`, `b`, ...). Refused for an author in a paired matchup, for a missing answer, and for your own Last Bit answer |
| `TutorialStep`, `TutorialDone`, `SetPaused` | host (leader may skip) | as in Turing Town |

## Configuration

The optional `WittyBits` section (`WittyBitsOptions`):

| Key | Default | Meaning |
|---|---|---|
| `TutorialMaxSeconds` | 120 | |
| `WritingSeconds` / `LastBitWritingSeconds` | 75 / 50 | |
| `MatchupSeconds` / `MatchupResultSeconds` / `ResultsSeconds` | 25 / 7 / 25 | |
| `PairRounds` | 2 | paired rounds before the Last Bit |
| `AnswerMaxLength` | 80 | |
| `MatchupPoints` / `SweepBonus` / `ForfeitPoints` | 1000 / 250 / 500 | each times the round |
| `LastBitPointsPerVote` | 400 | |
| `BotAnswerMinDelayMs` / `BotAnswerMaxDelayMs` | 6000 / 25000 | |
| `BotVoteMinDelayMs` / `BotVoteMaxDelayMs` | 2000 / 9000 | |

## Client

- Phones: the Writing form is a real HTML form (like Turing Town's onboarding); each answer is sent on its own. The phone
  remembers what it wrote so it can mark "(yours)" in the Last Bit; the server enforces it anyway.
- TV: the prompt on a plate, answer cards (two side by side, or a grid for the Last Bit), then authors, votes and points.
- Music borrows Turing Town's tracks for now (`minigames/witty-bits/music.js`).
