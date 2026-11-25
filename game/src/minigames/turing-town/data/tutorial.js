// The how-to-play shown on the TV before the game starts, told as the story of the town. Plain data: see
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
    {
        id: 'survey',
        type: 'animation',
        animation: 'survey',
        narration: 'tt_vo_survey',
        title: 'The town survey',
        caption: 'First, the town survey. Answer a few questions about yourself. Don\'t worry: nobody else ever sees your answers.',
        durationMs: 6000,
    },
    {
        id: 'square',
        type: 'animation',
        animation: 'chatter',
        narration: 'tt_vo_square',
        title: 'The town square',
        caption: 'Then head to the town square and chat. Ask questions. Listen for anyone who sounds a little too… computed. And stay anonymous: no real names, no personal details.',
        durationMs: 7500,
    },
    {
        id: 'vote',
        type: 'animation',
        animation: 'vote',
        title: 'Three rounds',
        caption: 'Quick Fire, then the Hot Seat, then the Final. The first two votes only fill the suspicion meter. Nobody leaves yet.',
        durationMs: 8000,
    },
    {
        id: 'stakes',
        type: 'animation',
        animation: 'standoff',
        title: 'One vote decides it',
        caption: 'The final vote is the only one that counts. Catch a bot and the humans win. Miss, and the town belongs to them.',
        durationMs: 8000,
    },
];

/** Narration audio key -> file under public/audio. Loaded in the background on the TV only (see ../music.js). */
export const NARRATION = Object.fromEntries(TUTORIAL_STEPS.filter(step => step.narration).map(step => [step.narration, `turing-town/narration/${step.id}.mp3`]));
