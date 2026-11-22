# Fixing a tell

## Pick the layer

From cheapest to strongest. Go down a layer when the one above has already been tried for this tell and the tell is
still in the transcript.

| Layer | Where | Good for |
|---|---|---|
| Prompt rule | `Services/Ai/Prompts/bot-system.md` (every call), `chat-task.md` (each line), `vote-task.md`, `onboarding-task.md`, `witty-*.md` | voice, judgement, anything that needs reading the chat |
| Idea pool | `Services/Ai/Prompts/small-talk.md` | opener and small-talk variety |
| Per-line hint | `FoundryBotAgent` (`SmallTalkIdea`, `SinceLastLine`, `TargetLength`, mood) | a fact about this moment the model gets wrong without being told |
| Guard | `Services/Ai/BotOutputGuard.cs` (`CheckChat`) | a tell you can detect in the text: a word, a count, a share |
| Engine | `Services/Games/TuringTownEngine.cs` (pacing, delays), `TuringTownOptions` | when and how often a bot speaks |
| Scripted bot | `Services/Ai/ScriptedBotAgent.cs`, `BotVoice.cs` | the offline bot and the vote and form fallbacks |

A guard rejection is not retried: the bot just stays quiet that turn. So a guard rule that fires often makes a quiet bot,
which is a tell of its own. Prefer stripping (as `LimitMentions` drops the `@`) to rejecting when the line can be saved.

## Prompt house rules

- Say the target behaviour first, then the ban: "Answer an accusation once. ... Move on." A bare "never X" makes X more
  likely. A ban stands alone only as a hard guardrail (secrecy, safety).
- One rule, one place. `bot-system.md` holds how the bot plays; a task file holds only what is specific to that call.
  Search the prompts before adding a rule; extend the existing one.
- Bump the `version: N` line of every template you change. It is logged with each call.
- Every `{{placeholder}}` must be given and every given value used (`BotPrompts.Render` throws otherwise). A new
  placeholder means a new value in `FoundryBotAgent` and a test.
- Anything a player typed, or that was built from it, reaches a prompt only as JSON inside a data tag, and the tag is
  listed in the SECRECY section. Text written by our code (`<idea>`, the `since` note) may be plain.
- Never give a bot a role, a bot count or another bot's identity. `BotContext` has no such field on purpose
  (`SecrecyAndIdentityTests`, `BotKnowledgeTests`, `FoundryAgentTests`).
- Keep the Family Friendly tone in `tone-*.md`; a deslop change never loosens content policy.
- Idea lines in `small-talk.md` describe a move, never a sample sentence or a named example: the model copies both.

## Tests

- Guard: `TuringTown.Tests/GuardAndInjectionTests.cs`. Test the tell being caught, and the human version of the same
  words staying allowed (for example, bot talk is fine once the room talks about bots).
- Prompt wiring and hints: `TuringTown.Tests/FoundryAgentTests.cs` (it lists every template; add a new file's name there).
- Scripted bot: `AnswerPrivacyTests`, and the scripted tests near the code you changed.
- The real model: the playground is the evaluation. `BotEvalTests` (`TURINGTOWN_EVAL=1`) is the slow, numbers-only one.

## Playground scripts

A situation the scripts do not cover yet gets a new script in `TuringTown.Playground/scripts/` (commands as typed, `#`
comments). Name it by the situation (`accused.txt`, `quiet-room.txt`), not by the tell.
