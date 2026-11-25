import {narrationEntry} from '../../../narration/registry.js';

// The short opening shown on the TV before the game starts. The rules are explained as the game goes (./transitions.js). Plain data: see
// core/tutorial/TutorialPlayer.js for the step format and core/tutorial/animations.js for the named animations.
// The phones show the same title and caption, and the leader's phone can skip it.
//
// Each step has an announcer line (`narration`), and its caption is the line's exact words, so it doubles as subtitles.
// `durationMs` is the step's minimum time on screen: a step also waits for its line to finish, plus a short pause.
export const TUTORIAL_STEPS = [
    {
        id: 'welcome',
        type: 'animation',
        animation: 'town',
        narration: 'welcome',
        title: narrationEntry('welcome').title,
        caption: narrationEntry('welcome').transcript,
        durationMs: 6000,
    },
    {
        id: 'infiltration',
        type: 'animation',
        animation: 'disguise',
        narration: 'infiltration',
        title: narrationEntry('infiltration').title,
        caption: narrationEntry('infiltration').transcript,
        durationMs: 7000,
    },
];

