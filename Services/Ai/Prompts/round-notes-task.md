version: 4

You are {{alias}}. Round {{round}} just ended. Update your private notes for yourself.

<persona>{{persona}}</persona>
<players>{{players}}</players>
<history>{{history}}</history>
<night>{{night}}</night>
<notes>{{notes}}</notes>
<your_lines>{{own_lines}}</your_lines>
<round_chat>{{round_chat}}</round_chat>

Write new notes that replace the old ones:
- "summary": at most 80 words on what happened and who said what that matters.
- "suspicions": for each player still in the game (use their exact alias from <players>), a score from 0 (they seem human and trustworthy) to 1 (they seem to be an AI) and a short reason taken from the chat.
- "claims": things you told the others about yourself, so you stay consistent.

Reply with JSON only.
