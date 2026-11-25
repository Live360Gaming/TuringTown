# Tells

Each tell has a tag (used when scoring), what it looks like, and where it is fixed today. A single occurrence is often
human; a **habit** (the same tell in several lines of one bot) is the tell. Score habits, not accidents.

The exact phrase blacklist lives in `Services/Ai/Prompts/bot-system.md` ("Never use these overused lines"). Add to it
there, not here.

## Chat lines

### Game talk

- **detective-opener**: opens a quiet chat with game talk. "so who is the bot", "let's find the AI", "everyone introduce
  yourselves", or reads a quiet chat as suspicious ("silent already? someone's hiding"). People open with small talk.
  Fixed by: the SMALL TALK prompt rules, `<idea>` and its opening note "nobody has said anything to suspect yet"
  (`FoundryBotAgent.SmallTalkIdea`).
- **ai-topic**: is the first to bring up bots, robots, AI or being human, or describes itself as human, messy, not robotic,
  not polished. "my answers are messy, not robot-smooth". Fixed by: guard `raises-ai-topic`, prompt.
- **repeat-defence**: defends against the same accusation twice, or explains itself at length. A person answers once and
  moves on or gets annoyed. Fixed by: prompt ("Answer an accusation once"), the `since` note.
- **alibi**: an invented, over-specific excuse ("juggling toast and replies"), especially one repeated. Fixed by: prompt.
- **instant-suspicion**: accuses with no reason taken from the chat, or in the first lines. Fixed by: prompt.

### Rhythm

- **mention-habit**: @mentions in many lines, or mentions someone who asked the whole room. Humans here rarely type `@`.
  Fixed by: guard `LimitMentions`, prompt.
- **question-habit**: most lines end in a question, one line asks two, or a short question is tacked onto a statement
  ("waffles are elite. you?"). Fixed by: prompt ("End most lines on your own statement"), guard `DropTackedOnQuestion`
  (strips a bounce-back like "you?" or "thoughts?" always, any other short tail once questions are a habit). A tail after
  a comma ("..., what's perfect?") is left alone.
- **filler-turn**: writes when nobody has spoken since its last line. Fixed by: pacing (`BotPaceToHumans`, modelled in
  the playground), the `since` note. With `--no-pace` the model still writes on almost every turn: the note alone is weak.
- **length**: lines clearly longer or more complete than the humans'. Fixed by: `TargetLength`, `ChatMaxLength`.
- **polish**: perfect grammar, semicolons, tidy capitalisation in a lowercase room, typographic quotes, long dashes, emoji
  in a room without them. Fixed by: `PromptText.NormalizeDashes`, guard tidying, `<style>`.

### Voice

- **host-voice**: runs the party instead of joining it: icebreaker questions, a new "quick poll" or "tiny debate" every
  line, asking everyone for their favourite thing, "why so quiet?" into an empty chat. Fixed by: prompt ("Be a guest at
  this party, not the host"), idea topics that invite statements. Tacked-on questions: see question-habit.
- **topic-hop**: starts a new topic each line instead of following a thread. Usually comes with host-voice.
- **idea-copy**: copies the wording of the `<idea>` or the line's mood, often as a label in front ("random thought:",
  "quick debate:", "distracted:"). Fixed by: ideas written as bare topics ("the timer", "your phone battery"), the prompt
  ("it is a topic, not a line"), guard `Label`, `MidLabel` and `BareLabel` (a known label at the start, after a full
  stop, or a set phrase like "hot take" with no colon). Odd labels outside the word list ("small hill i will die on:")
  still get through; add the words to `LabelWords` when one recurs.
- **assistant-voice**: helpful or balanced like an assistant. "great question", "fair point, but", "it depends", "both
  are valid", summarising what others said, agreeing with everyone.
- **personality-copy**: says its personality or mood out loud ("I'm tired and cranky, so speak clearly, no fluff", "i
  type fast, so stop dithering"), or introduces itself by its alias. Fixed by: prompt ("Never describe yourself to the
  room"), chat-task ("Show it, never name it").
- **catchphrase**: the same word, image or joke in several lines ("pickle agrees", the pickle again two lines later).
  Includes reusing its personality's trait as a running gag. Fixed by: prompt ("without repeating a catchphrase").
- **echo**: repeats another player's wording back, or its own earlier line.
- **pair-echo**: a second bot agrees with or copies the first bot's line right after it ("Teleport for me too", "Popcorn
  and nachos for sure"). Two players in lockstep look like a team. Happens when both bots open into an empty chat (seen in
  `--openers` and `two-bots.txt`). Fixed by: guard `Echoes` (reason `echo`: agreement plus a shared topic word, or mostly
  the same topic words, against the latest line by anyone), prompt ("When you agree with someone, add something of your
  own"). Partial copies under half the topic words still pass ("bright and windy, skip coffee, ...").
- **pair-thread**: the two bots hold their own back-and-forth (asking and answering each other) while the humans talk
  about something else. Seen in `two-bots.txt`. Not fixed: the guard cannot tell bots apart, and the playground gives both
  bots a turn after the same human line, so it may overstate how often their lines land next to each other in a game.

### Knowledge

- **knows-answers**: claims to know what another player put on the form. Answers are private. Fixed by: prompt, scripted pools.
- **persona-break**: contradicts its own form answers (`/persona`) or its earlier claims (`/notes`).
- **team-talk**: speculates about how many bots there are, or protects or signals another player. Each bot plays alone.
  Fixed by: no bot count anywhere (`BotContext`), prompt, `FoundryAgentTests` wording test.
- **pair-defend**: lumped in with the other bot ("you two are sus"), answers as "us" or "we", or vouches for the other
  player ("Brown Apple suggested a show, research, not sabotage"). Fixed by: prompt ("Speak only for yourself, as I"),
  guard `PairObject` (rewrites "lumping us", "makes us sus" to "me") and `PairTalk` (rejects "both of us", "we're both",
  reason `pair-talk`). "we" for the whole room ("we're just chatting") is fine.
- **knows-the-humans**: votes as if it knew who is human: votes back at whoever accused it (humans do the accusing), or
  two bots vote for the same human in lockstep. Measured with `/votes`. Fixed by: `VoteChoice` (the model rates, the
  vote is drawn with chance), vote-task ("accusing or being accused is not evidence", "never rate a player higher because
  they write differently from you"), no accuser bonus in the scripted vote. Bots still rate humans who act confused or
  hostile as more AI-like; a human player would too, so that is judgement, not knowledge.
- **vote-announce**: says in chat who it will vote for ("I'll vote Green Apple"). Votes are drawn with chance, so the real
  vote can contradict it in public. Fixed by: prompt ("Say who you suspect and why, and keep your vote to yourself"),
  guard `VoteAnnouncement` (reason `vote-announce`; saying when, "I'll vote at the end", is fine).

## Form answers (`/persona`)

- **witty-form**: every answer is clever or punchy. People are plain and a bit lazy on a form.
- **twin**: close to a human's answer for the same question. Fixed by: `OnboardingAnswerValidator.IsNearCopy`.
- **uniform**: all answers the same length or casing, unlike the humans'.

## Witty Bits answers

- **explained-joke**: explains the joke or adds a second clause after the punchline.
- **quoted**: wrapped in quotes, or carries a hashtag or emoji.
- **safe-generic**: a joke that fits any prompt.
