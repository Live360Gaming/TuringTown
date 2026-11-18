# Content policy (Family Friendly mode)

The host sets **Family Friendly** per session (default ON). The server stores it on the `Room` and exposes it as
`Room.ContentPolicy` (`Models/ContentPolicy.cs`). Any system that produces content for a room must take a
`ContentPolicy` argument so the setting cannot be forgotten.

## What ON / OFF mean

- **ON**: mature content is restricted.
- **OFF**: game-content restrictions are relaxed (e.g. crude humor, mild profanity). It does **not** disable mandatory
  safety protections (hate, harassment of individuals, sexual content involving minors, self-harm, illegal content).
  Those stay enforced at the model/content-safety layer in both modes.

## Enforcement strategy for future content systems

A system prompt alone is not sufficient. When AI generation (Azure AI Foundry) or other content is added:

1. Add a per-request prompt addendum derived from `ContentPolicy`.
2. Set Azure AI Content Safety / Foundry filter severity thresholds per mode (stricter when ON).
3. Check generated output server-side before broadcasting it to host and phones.
4. Tag static content pools (prompts/questions) with a `mature` flag and filter by policy.
5. Run any player-submitted text through the same policy and filters.

Nothing above is implemented yet; only the setting and `ContentPolicy` extension point exist.
