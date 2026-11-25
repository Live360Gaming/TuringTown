// The announcer's script. Each beat of the game is explained just before it is played (the server's Transition stage),
// and the caption is the spoken line word for word, so it doubles as subtitles. Phones show the same words.
// The audio key of a line is tt_vo_<id>; a line without a recording is shown as its caption for `durationMs`.

import {AUDIO_MIX} from '../../../core/music.js';

const line = (title, caption, animation, durationMs = 6000) => ({title, caption, animation, durationMs});

export const TRANSITIONS = {
    survey: line('The town survey', 'First, the town survey. Answer a few questions about yourself. Don\'t worry: nobody else ever sees your answers.', 'survey'),
    rapid: line('Five quick questions', 'Now, five questions and two minutes. This time the whole town sees what you write, so answer like yourself. No real names, and nothing personal.', 'chatter', 8500),
    tap: line('Who seems off?', 'Time\'s up. Read your neighbors carefully, then tap the one who seems a little off. Nobody leaves yet. But the town remembers.', 'vote', 8000),
    meter: line('The suspicion meter', 'The town has spoken. Well, whispered. The higher your meter, the more eyes are on you.', 'pulse', 6000),
    topic: line('One more question', 'One more question. Nobody sees your answer just yet. One of these may come back to haunt somebody.', 'survey'),
    hotseat: line('The hot seat', 'Somebody is taking the hot seat. Suspicion loads the dice, but luck rolls them.', 'shuffle', 5500),
    answer: line('Did they say that?', 'Here is the answer under their name. Careful: it is probably not theirs. Question them, and see how they handle it.', 'blank', 7500),
    discussion: line('The town square', 'To the town square. Everything is fair game: the answers, the meter, the hot seat. Talk it out.', 'chatter'),
    final: line('One vote', 'This is the only vote that counts. Catch a bot and the humans win. Miss, and the town belongs to them.', 'standoff', 7000),
    verdict: line('The verdict', 'The votes are in. Let\'s see who has been living next door.', 'pulse', 4500),
};

export const ENDINGS = {
    humans_win: line('Humans win', 'Got one! The town is safe tonight. Lock your doors anyway.'),
    bots_win: line('The bots win', 'Oh dear. That was one of your own. The bots would like to thank you for your hospitality.'),
    no_verdict: line('No verdict', 'No verdict. The town went to bed arguing, and the bots slept very well.'),
    truth_theirs: line('The truth', 'And that hot seat answer? It really was theirs.'),
    truth_not_theirs: line('The truth', 'And that hot seat answer? Somebody else wrote it.'),
};

export const narrationKey = id => `tt_vo_${id}`;

/** Says an ending line on the TV, if it has been recorded. */
export function sayLine(scene, id) {
    const key = narrationKey(id);
    if (scene.cache.audio.exists(key)) scene.sound.play(key, {volume: AUDIO_MIX.narrationVolume});
}

// The lines that have a recording in public/audio/turing-town/narration/<id>.mp3. Add an id here once its file exists.
const RECORDED = ['welcome', 'infiltration', 'survey'];

/** Narration audio key -> file under public/audio. Loaded in the background on the TV only (see ../music.js). */
export const NARRATION = Object.fromEntries(RECORDED.map(id => [narrationKey(id), `turing-town/narration/${id}.mp3`]));
