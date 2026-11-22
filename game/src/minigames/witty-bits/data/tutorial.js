// The Witty Bits how-to-play shown on the TV. Plain data: see core/tutorial/TutorialPlayer.js for the step format and
// core/tutorial/animations.js for the named animations. The phones show the same title and caption.
export const TUTORIAL_STEPS = [
    {
        id: 'welcome',
        type: 'animation',
        animation: 'pulse',
        title: 'Welcome to Witty Bits',
        caption: 'The funniest answer wins. Somewhere among you, an AI is trying very hard to be funny.',
        durationMs: 6000,
    },
    {
        id: 'write',
        type: 'animation',
        animation: 'survey',
        title: 'Write',
        caption: 'You get two prompts on your phone. Write the funniest short answer you can. No names or personal details.',
        durationMs: 7000,
    },
    {
        id: 'faceoff',
        type: 'animation',
        animation: 'shuffle',
        title: 'Face off',
        caption: 'Two answers to the same prompt go head to head, with no names. Everyone who did not write them votes.',
        durationMs: 7000,
    },
    {
        id: 'points',
        type: 'animation',
        animation: 'vote',
        title: 'Score',
        caption: 'Points are shared by votes. Get every vote for a bonus. Round two is worth double.',
        durationMs: 6500,
    },
    {
        id: 'lastbit',
        type: 'animation',
        animation: 'chatter',
        title: 'The Last Bit',
        caption: 'Finally, everyone answers one prompt and every vote counts. Your points go to your team: Humans or AI.',
        durationMs: 7000,
    },
];
