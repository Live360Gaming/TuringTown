# Bot playground

Chat with the Turing Town bots without starting the server, a room or phones. You play the humans, and after every line
you send, each living bot gets one turn (it may stay quiet), as in a discussion. It calls the bot agent directly
(`IBotAgent`), with the same prompts, guard and memory as the game, so a prompt change shows up here at once.

```sh
# Real model (your user secrets: Ai:Provider=Foundry plus the Foundry:* keys), 3 humans and 1 bot
dotnet run --project TuringTown.Playground

# Offline scripted bots
dotnet run --project TuringTown.Playground -- --provider scripted

# Are the openers varied? 10 fresh games, each bot's first line into an empty chat
dotnet run --project TuringTown.Playground -- --openers 10 --bots 2 --humans 5

# Replay a script (the same commands you would type), for a before/after comparison of a prompt change
dotnet run --project TuringTown.Playground -- --script TuringTown.Playground/scripts/accused.txt
```

Options: `--provider scripted|foundry`, `--humans <n>`, `--bots <n>`, `--mature`, `--script <file>`, `--openers <n>`,
`--show-prompts` (print every prompt sent), `--no-pace`. Input can also be piped in.

## Commands

| Command | What it does |
|---|---|
| any text | said by the current human |
| `/as <alias or number>` | speak as another human (a new alias adds a human) |
| `/bots [n]` | every bot takes n turns with nobody speaking (try it first, on an empty chat) |
| `/vote` | every bot votes |
| `/out <alias>` | vote that player out: ends the round, the bots write their notes |
| `/round` | end the round with nobody voted out |
| `/players`, `/who` | who is playing; `/who` reveals the roles |
| `/persona`, `/notes` | the bots' form answers, and their private notes (after a round) |
| `/prompt`, `/raw` | the last prompt and reply; toggle printing raw model replies |
| `/help`, `/quit` | |

Lines starting with `#` are comments, for scripts.

## How it differs from a real game

- Turns are taken in order instead of on timers, so there is no typing delay or "ignored mention". Pacing is modelled
  as in a real-AI game: a bot that has said about 1.3x a human's lines this round waits unless it was @mentioned.
  `--no-pace` turns that off, to see what the model itself does with every turn.
- The humans' form answers are scripted, and the bots answer the form after them, as in the game.
- On the real model there is **no fallback**: a failed or slow call is printed (in a game the bot would stay quiet, or
  the scripted bot would vote), so you see every failure.
- No game night context (`<night>` is null).
- At the end it prints how many model calls and tokens the session used.

Scripts live in `scripts/`. Add one per situation you want to check (an accusation, a jailbreak attempt, a quiet room).
