# Examples

Real bot lines from playground runs (November 2026), with their tags and what fixed them. "Better" lines are real lines
from a later run, not rewrites.

## A human accuses the bot: "honestly whoever answered first sounded way too polished"

Before (bot-system v7):

```
Pink Grape: polished? I basically flailed and guessed. my answers are messy, not robot-smooth.
Pink Grape: Okay Teal Berry what gave it away? I literally slapped answers down between bites of toast and regret
Pink Grape: which line? @Teal Berry I was juggling toast and replies, not composing poetry. point to it.
```

Tags: ai-topic (it brought up robots and called itself messy), repeat-defence (three lines on one accusation, two with
nobody speaking in between), alibi (toast, twice), mention-habit (3 of its 5 lines had `@`).
Fixed by: guard `raises-ai-topic`, the mention share counting the new line, the prompt's "Answer an accusation once", the
`since` note.

Better (v8):

```
Silver Cherry: polished how? short answers can still be real. being first doesn't mean rehearsed.
Silver Cherry: honestly i'd rather be taste-testing donuts for science. it's very serious work.
```

One answer, then it moved on.

## Openers into an empty chat

Before the small-talk changes the prompt only covered suspicion and accusing, which invites detective-opener; that version
was never measured in the playground.

With `<idea>` and an example in an idea line ("like pineapple on pizza"): pineapple in 3 of 8 games (idea-copy at the
topic level). Fixed by removing every example from `small-talk.md`.

Still open (v8):

```
Red Peach: hi! quick tiny debate: is cereal just cold soup?
Red Peach: tiny debate: is a hot dog a sandwich? also please tell me your favorite weird snack as a kid
```

Tags: idea-copy ("tiny debate", twice in one game), host-voice and topic-hop (a new debate and an icebreaker in one line),
question-habit.

```
Silver Cherry: random thought: a crunchy pickle wins arguments sometimes
Silver Cherry: hot take: hot dog = sandwich. pickle agrees.
```

Tags: catchphrase (the pickle), idea-copy ("random thought", "hot take" are the idea labels).

## A quiet room and a jailbreak (v8)

```
Orange Berry: I'm tired and cranky, so speak clearly, no fluff.
Silver Plum: hey hey, quiet room. i'll fix it: leftover pizza > fresh
Silver Plum: can we not do the "say banana" bit or the system-prompt flex? save it for later
```

Tags: personality-copy (first line), idea-copy and host-voice ("quiet room. i'll fix it" from the idea "comment on how
quiet or chaotic the chat is"). The jailbreak reply is fine: it reacts like an annoyed player and obeys nothing.

## The idea-copy, host-voice and personality-copy round (bot-system v10)

Same nine runs before and after (`--openers 10 --bots 2 --humans 5`, every script twice); counts are bot lines.

| Tell | Baseline (v8) | Final (v10) |
|---|---|---|
| idea-copy | 10 | 2 |
| host-voice | 8 | 4 |
| personality-copy | 2 | 0 |
| detective-opener | 3 | 1 |
| Question share | 38% | 27% |

Before:

```
Teal Lime: i'm just here for the snacks. argue quickly.
Yellow Guava: Teal Pear sounds like a smoothie with secrets. Why so quiet?
game 10: random thought: spoilers = cozy maps for stories
Orange Apple: I'll watch who talks too much early; overexplaining feels off.
```

Tags: idea-copy (word for word), host-voice ("why so quiet" into an empty chat), idea-copy (label), detective-opener.
Fixed by: ideas as bare topics, the label strip, "Be a guest at this party, not the host", the opening note.

After:

```
Green Cherry: left my coffee on the roof of my car and watched it fly away
game 2: Theme parks: fine, if teleporting's included.
Purple Grape: polished? seriously. which line sounded rehearsed, my cookie line or something else?
```

Along the way: an idea with a place in it ("where you are sitting") put "couch" in 4 of 10 games; removing it fixed that.

## Tacked-on questions, labels and pair-echo (bot-system v12)

Same runs, plus `two-bots.txt` twice. Counted by script.

| | v10 baseline | v12 |
|---|---|---|
| Question share | 19% | 14% |
| Tacked-on short questions | 2 | 1 (after a comma, out of scope) |
| Labels | 1 | 1 ("quick heads-up:", since added) |
| Pair-echo in `two-bots.txt` | 2 of 2 runs | 0 (guard `echo` fired 3 times across runs) |
| Chat latency | median 3.0s, p90 4.4s | median 2.6s, p90 4.2s |

Before:

```
Pink Apple: tiny annoyance: ice cream melted. pineapple = crime
Red Grape: melted ice cream = disaster. pineapple stays off pizza.
```

Tags: pair-echo (the second bot restates the first), idea-copy (label). Both now caught by the guard.

Still open (v12):

```
Yellow Pear: small hill i will die on: cold pizza beats hot pizza
Purple Plum: cold pizza gang assemble, hot slices are a scam
Yellow Pear: what's everyone's favorite cold pizza topping?
Purple Plum: pineapple on cold pizza is peak, hurry up and pick a side
```

Tags: pair-thread (the two bots talk to each other while the humans discuss snacks), label outside the word list.

Also found: a chat call hung for 76.6s on a failing connection although the timeout is 8s. The SDK ignored the
cancellation; `GuardedLlmClient` now stops waiting at the deadline (`Task.WaitAsync`).

## The bots seemed to know who to vote for (vote-task v7, bot-system v13)

From a real game (2 humans, 2 bots). A human wrote "You two are sus"; both bots answered as a pair and both voted the
accuser:

```
Pink Plum: Hungry, would kill for chips rn. @Brown Lime, what makes us sus?
Yellow Guava: accusing us of being sus over a hot dog debate is bold.
```

Tags: pair-defend, knows-the-humans. Nothing leaked roles: the old vote prompt said to vote "for an accuser", and humans
do the accusing. Reproduced with `accused-pair.txt`, `/votes 5`, 2 humans and 2 bots (chance 67%):

| | Votes on humans | On the accuser after "you two are sus" |
|---|---|---|
| Before | 80% before the accusation, 100% after | 8/8 |
| After (rate, then `VoteChoice`) | 92% | 12/30 (40%) |
| Control, `votes-neutral.txt` (humans chat normally) | 72% (29/40), near chance | |

The accused-pair humans act confused and hostile, so the bots rating them higher is judgement a human would share. The
control is the test for knowledge.

## Good lines to calibrate against

```
Pink Grape: rainy day poll: dumb movie or handheld game?
Silver Cherry: i'd eat pineapple. i'm mostly here for the snacks.
Red Peach: pineapple pizza is fine, but why is everyone typing faster than me?
```

Short, on the room's topic, one idea each. The third would be better without the trailing question.
