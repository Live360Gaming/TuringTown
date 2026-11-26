# Shout Out

A buzz-in trivia game played as part of the [game night](game-night.md). The room votes for categories, then each
question shows on the TV with four answers. The first player to buzz in on their phone picks one: right earns points,
wrong costs points and the question opens up for everyone else. The last questions are worth double. Bots play on
hidden seats like everyone else.

The server runs it (`Services/Games/ShoutOutEngine`); the client screens are in `game/src/minigames/shout-out/`.

## Stages

```
Tutorial → Categories → (Question ⇄ Buzzed → AnswerReveal) × Questions → Results
```

| Stage | What happens | Ends when | `data` |
|---|---|---|---|
| Tutorial | The TV plays the how-to-play while the trivia is written | host calls `TutorialDone` or the leader skips (or the safety limit) | none |
| Categories | Everyone (bots too) picks up to `MaxCategoryPicks` of `CategoryChoices` categories | everyone has voted, or time runs out | `{categories: [{id, name}], maxPicks}` |
| Question | The question and its four answers. Buzzing opens at `buzzOpensAtUtcMs` (after `ReadSeconds`, at once when it reopens) | someone buzzes, or time runs out | `{index, count, double, category, text, options: [{id, text}], buzzOpensAtUtcMs, out}` |
| Buzzed | The buzzer picks an answer on their phone | they answer, or `AnswerSeconds` pass | the same, plus `buzzerId`, `buzzerAlias` |
| AnswerReveal | The right answer, every buzz and how it went, the top 3 | timer | `{index, count, category, text, options, correctId, attempts: [{playerId, alias, optionId, correct, points}], leaders}` |
| Results | Everyone's points this game | host or leader calls `ContinueNight`, or timer | `{standings}` |

## Rules

- **Categories.** The two most picked (ties at random) plus one wildcard. The questions were written for every category
  on offer during the tutorial (twice as many as needed), and the vote picks among them, so nobody waits for the AI.
- **Buzzing.** The first buzz the server receives once buzzing is open wins. A buzz before then is refused and locks the
  player out for `EarlyBuzzLockoutMs`. A wrong answer (or no answer in time) costs `WrongPoints`, puts the player `out`
  for that question, and reopens it for `RebuzzSeconds`. Nobody buzzing reveals the answer with no points.
- **Points.** A right answer earns `CorrectPoints` (twice in the last `DoubleQuestions`). Each team scores the average
  of its seats (see [game-night.md](game-night.md#score)).
- The right answer's id never reaches a client before the AnswerReveal.

## Bots

Bots never call a model here: a model knows nearly every answer and would buzz at once, which would give it away.
`ShoutOutBotPolicy` decides instead. Each bot knows an answer `BotKnowledgeMin`..`BotKnowledgeMax` of the time (drawn
once per game, a little less on hard questions), buzzes quickly when it knows and rarely, slowly, when it guesses, and
sometimes stays quiet. It paces itself to the humans: when they buzz slowly, the bots slow down too. Bots also vote in
the category vote, so the progress count never says how many humans there are. `GetUnmasked` is always empty.

## Questions

`ITriviaGenerator` writes them. On the real AI, `FoundryTriviaGenerator` (prompts `trivia-system`, `trivia-task`) writes
a few more than needed, pitched at the room's categories and decades (`NightContext.RoomEras`, from Turing Town's
[era questions](game-flow.md), when Turing Town has already run), and a second call (`trivia-check-task`) answers each
one cold: any question it answers differently is dropped. `TriviaValidator` checks the rest (four distinct answers, a
valid right answer, lengths, a known category, nothing personal, nothing mature in a family room), every question and
answer passes `IContentFilter`, and `BuiltInTrivia` (a hand-written bank tagged by category and decade) fills any gap.
Offline, the built-in bank is all there is.

## Hub calls

| Method | Who | When |
|---|---|---|
| `ShoutOutCategories(string[] categoryIds)` | player | Categories. Can be changed until the vote ends |
| `Buzz()` | player | Question |
| `ShoutOutAnswer(optionId)` | the buzzer | Buzzed (`a` to `d`) |
| `TutorialStep`, `TutorialDone`, `SetPaused` | host (leader may skip) | as in the other games |

## Configuration

The optional `ShoutOut` section (`ShoutOutOptions`):

| Key | Default | Meaning |
|---|---|---|
| `TutorialMaxSeconds` / `CategoriesSeconds` | 90 / 20 | |
| `CategoryChoices` / `MaxCategoryPicks` | 6 / 2 | |
| `Questions` / `DoubleQuestions` | 10 / 3 | |
| `ReadSeconds` / `EarlyBuzzLockoutMs` | 3 / 1000 | |
| `BuzzSeconds` / `RebuzzSeconds` / `AnswerSeconds` | 10 / 6 / 6 | |
| `RevealSeconds` / `ResultsSeconds` | 5 / 25 | |
| `CorrectPoints` / `WrongPoints` | 500 / 250 | |
| `BotCategoryMinDelayMs` / `BotCategoryMaxDelayMs` | 2000 / 9000 | |
| `BotKnowledgeMin` / `BotKnowledgeMax` | 0.55 / 0.75 | |

## Client

- Phones: a big BUZZ button (grey while the room reads, red once it is open; Space works on a laptop) that vibrates,
  then four answer buttons for the buzzer only.
- TV: the question on a plate, the answers in a 2x2 grid, the buzzer's avatar, then the right answer in green and wrong
  picks in red.
- Music borrows Turing Town's tracks for now (`minigames/shout-out/music.js`).
