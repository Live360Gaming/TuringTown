// How each part of the game looks while the announcer explains it (the server's Transition stage). The words and the
// recordings are in narration/manifest.json; the caption is the line, so it doubles as subtitles. `durationMs` is the
// least time on screen, used on its own when the line cannot be played.
import {narrationEntry} from '../../../narration/registry.js';

const step = (key, animation, durationMs = 6000) => {
    const {title, transcript} = narrationEntry(key);
    return {title, caption: transcript, animation, durationMs};
};

export const TRANSITIONS = {
    survey: step('survey', 'survey'),
    rapid: step('rapid', 'chatter', 8500),
    tap: step('tap', 'vote', 8000),
    meter: step('meter', 'pulse'),
    topic: step('topic', 'survey'),
    hotseat: step('hotseat', 'shuffle', 5500),
    answer: step('answer', 'blank', 7500),
    discussion: step('discussion', 'chatter'),
    final: step('final', 'standoff', 7000),
    verdict: step('verdict', 'pulse', 4500),
};
