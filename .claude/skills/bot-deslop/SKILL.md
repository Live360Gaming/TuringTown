---
name: bot-deslop
description: Find and remove the tells that give Turing Town's AI bots away, so their chat lines, form answers and Witty Bits answers read like a person on a phone at a party. Use when changing a bot prompt (Services/Ai/Prompts), BotOutputGuard or a bot agent, when reviewing a playground transcript, or when bots "sound fake", "sound like AI" or "seem dumb".
---

# Bot deslop

A **tell** is anything in a bot's output that a sharp player could vote on: a habit, a phrase, a rhythm, a topic. The
goal is a transcript where the bot's lines carry no more tells than the humans' lines. Judge by what the room sees, never
by whether a single line is "good writing".

The bots are measured, not guessed at: every run goes through the playground (`TuringTown.Playground`, see its README),
which uses the game's own prompts, guard and memory.

## Steps

1. **Baseline.** Run, with the real model, and save each output to the scratchpad:
   - `dotnet run --project TuringTown.Playground -- --openers 10 --bots 2 --humans 5`
   - `dotnet run --project TuringTown.Playground -- --script TuringTown.Playground/scripts/<name>.txt` for every script in
     that folder, with the `--bots` / `--humans` each script's header names.

   Run each script twice: the model varies, and one run proves nothing. Run them one after another: parallel runs hit the
   deployment's token rate limit (HTTP 429), which shows up as failed calls. Build first (`dotnet build TuringTown.Playground`);
   a stale build tests the old prompt. The playground paces bots like the game does; add `--no-pace` only to measure
   what the model does with every turn (filler-turn).
2. **Score.** Tag every bot line against every tell in [references/tells.md](references/tells.md). Done when each bot line
   carries zero or more tags and the tally table (below) is filled in. Read the humans' lines too: a tell is relative to
   how this room writes.
3. **Fix** the worst tell first, one tell per change. Choose the layer and write the change by
   [references/fixing.md](references/fixing.md). A tell that survives a prompt rule gets a code backstop.
4. **Test.** Add or update a test for any guard or code change, then
   `dotnet test TuringTown.slnx --configuration Release`. Done when it is green.
5. **Compare.** Rerun exactly the step 1 commands and score again. Done when the targeted tell's count fell, no other tell
   rose above its baseline, and opener variety held. If a fix made the bot worse elsewhere, revert it rather than stack
   another rule on top.
6. **Report** the before/after tally, the lines that show the change, and any new tell you spotted but did not fix.

## Tally

| Measure | How | Healthy |
|---|---|---|
| Distinct openers | `--openers` summary line | every opener different, no topic in more than 2 of 10 games |
| Mention share | bot lines with `@` / bot lines | about 1 in 4 or fewer, except replies to someone who @mentioned the bot |
| Question share | bot lines ending in `?` / bot lines | under 1 in 3 |
| Tells per bot line | tags / bot lines | no higher than for the humans' lines |
| Guard rejects | `Guard rejects` line at the end of each run, by reason | rare; many mean the prompt and guard disagree |
| Failed calls | `chat call failed` lines (token limit, content filter, timeout) | none; each one is a silent bot in a game |
| Latency | `Latency Chat` line at the end of each run (median, p90, max) | p90 well under `Ai:Calls:Chat:TimeoutMs` |
| Votes on humans | `/votes` "All bots" line in `votes-neutral.txt` (and `accused-pair.txt`) | near chance in `votes-neutral` (67% with 2 humans, 2 bots); far above means a bot is reading roles from somewhere |
| Gut check | Would you vote this bot out from this transcript alone? Say why. | no |

Also read [references/examples.md](references/examples.md) before a first review: real lines from this game, tagged.
