// The Shout Out how-to-play shown on the TV. Plain data: see core/tutorial/TutorialPlayer.js for the step format and
// core/tutorial/animations.js for the named animations. The phones show the same title and caption.
export const TUTORIAL_STEPS = [
    {
        id: 'welcome',
        type: 'animation',
        animation: 'pulse',
        title: 'Welcome to Shout Out',
        caption: 'Buzz-in trivia. Know it? Hit the buzzer before anyone else. The AI is playing too, and it is not as clever as it thinks.',
        durationMs: 6500,
    },
    {
        id: 'categories',
        type: 'animation',
        animation: 'survey',
        title: 'Pick your categories',
        caption: 'First, pick up to two categories on your phone. The most popular ones make the cut, plus a wildcard.',
        durationMs: 6500,
    },
    {
        id: 'buzz',
        type: 'animation',
        animation: 'chatter',
        title: 'Buzz in',
        caption: 'Read the question, wait for the answers, then hit BUZZ. Too early and you are locked out for a moment.',
        durationMs: 7000,
    },
    {
        id: 'answer',
        type: 'animation',
        animation: 'vote',
        title: 'Answer fast',
        caption: 'First to buzz picks an answer. Right: points. Wrong: you lose some, and everyone else gets a go.',
        durationMs: 7000,
    },
    {
        id: 'double',
        type: 'animation',
        animation: 'shuffle',
        title: 'Double points',
        caption: 'The last questions are worth double. Your points go to your team: Humans or AI.',
        durationMs: 6000,
    },
];
