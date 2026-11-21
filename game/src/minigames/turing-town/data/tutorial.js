// The how-to-play shown on the TV before the game starts, told as the story of the town. Plain data: see
// core/tutorial/TutorialPlayer.js for the step format and core/tutorial/animations.js for the named animations.
// The phones show the same title and caption, and the leader's phone can skip it.
export const TUTORIAL_STEPS = [
    {
        id: 'welcome',
        type: 'animation',
        animation: 'town',
        title: 'Welcome to Turing Town',
        caption: 'A quiet little town. Friendly neighbors. Nothing strange going on… right?',
        durationMs: 6000,
    },
    {
        id: 'infiltration',
        type: 'animation',
        animation: 'disguise',
        title: 'Someone moved in',
        caption: 'AI bots have slipped into town, disguised as residents. They look like you, they type like you, and they want to stay.',
        durationMs: 7000,
    },
    {
        id: 'survey',
        type: 'animation',
        animation: 'survey',
        title: 'The town survey',
        caption: 'First, answer a few questions about yourself. Nobody else ever sees your answers.',
        durationMs: 6000,
    },
    {
        id: 'square',
        type: 'animation',
        animation: 'chatter',
        title: 'The town square',
        caption: 'Then chat with everyone. Ask questions, and listen for anyone a little too… computed. Stay anonymous: no real names or personal details.',
        durationMs: 7500,
    },
    {
        id: 'vote',
        type: 'animation',
        animation: 'vote',
        title: 'Town meeting',
        caption: 'When the clock runs out, vote out the resident you trust least. Their true identity is revealed.',
        durationMs: 7000,
    },
    {
        id: 'stakes',
        type: 'animation',
        animation: 'standoff',
        title: 'Trust no one',
        caption: 'Humans win by unmasking every bot. The bots win if they can outlast you. Good luck, citizen.',
        durationMs: 6500,
    },
];
