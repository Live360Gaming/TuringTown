// The how-to-play shown on the TV before the game starts. Plain data: see core/tutorial/TutorialPlayer.js for
// the step format. Replace the placeholder animations and slides with real ones (or a `video` step) later.
export const TUTORIAL_STEPS = [
    {
        id: 'welcome',
        type: 'animation',
        animation: 'shuffle',
        caption: 'Some of the players in this town are AI bots pretending to be human.',
        durationMs: 6000,
    },
    {
        id: 'chat',
        type: 'animation',
        animation: 'pulse',
        caption: 'Chat about the questions. Stay anonymous! No names or personal details.',
        durationMs: 6000,
    },
    {
        id: 'vote',
        type: 'slide',
        caption: 'Then vote someone out. Humans win by voting out every bot. The bots win by surviving.',
        durationMs: 6000,
    },
];
