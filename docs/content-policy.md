# Content policy (Family Friendly mode)

The host sets **Family Friendly** per session (default ON). The server stores it on the `Room` and exposes it as
`Room.ContentPolicy` (`Models/ContentPolicy.cs`). Any system that produces content for a room must take a
`ContentPolicy` argument so the setting cannot be forgotten.

## What ON / OFF mean

- **ON**: mature content is restricted.
- **OFF**: game-content restrictions are relaxed (e.g. crude humor, mild profanity). It does **not** disable mandatory
  safety protections (hate, harassment of individuals, sexual content involving minors, self-harm, illegal content).
  Those stay enforced at the model/content-safety layer in both modes.

A nonempty OFF-mode onboarding form includes at least one cheekier question marked mature, including when built-in
questions are used. The configured content filter still checks those questions and their options. If no acceptable form
can be assembled, the game returns to the lobby with an error rather than showing rejected content.

## Enforcement strategy for future content systems

A system prompt alone is not sufficient. When AI generation (Azure AI Foundry) or other content is added:

1. Add a per-request prompt addendum derived from `ContentPolicy`.
2. Set Azure AI Content Safety / Foundry filter severity thresholds per mode (stricter when ON).
3. Check generated output server-side before broadcasting it to host and phones.
4. Tag static content pools (prompts/questions) with a `mature` flag and filter by policy.
5. Run any player-submitted text through the same policy and filters.

Status: the setting, `ContentPolicy`, and every item above except the Foundry filter thresholds are implemented. The prompt addendum is the
`tone-family.md` / `tone-mature.md` templates; the static pools carry `mature` flags and `QuestionValidator` enforces them (for the options too);
generated and player text goes through `IContentFilter`, which is `SeverityContentFilter` (Azure AI Content Safety) when
`ContentSafety:Provider=Azure`, stricter for Family Friendly, and a mature room relaxes only the Violence category. Setting the filter thresholds
on the Foundry *deployment* is an Azure setting, not code. See [ai-agents.md](ai-agents.md).
With the default `ContentSafety:Provider=None`, the filter allows all text; the setting is not a profanity filter.

The Turing Town show host (`IShowHost`) takes the room's `ContentPolicy`; its questions, topic and lines pass the same
validator and content filter as the onboarding form.
