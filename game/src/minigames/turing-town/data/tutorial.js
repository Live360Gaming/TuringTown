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
        narration: 'tt_vo_welcome',
        title: 'Welcome to Turing Town',
        caption: 'Welcome to Turing Town. A quiet little town. Friendly neighbors. Nothing strange going on here… right?',
        durationMs: 6000,
    },
    {
        id: 'infiltration',
        type: 'animation',
        animation: 'disguise',
        narration: 'tt_vo_infiltration',
        title: 'Someone moved in',
        caption: 'Wrong. AI bots have slipped into town, disguised as residents. They look like you. They type like you. And they want to stay.',
        durationMs: 7000,
    },
];

