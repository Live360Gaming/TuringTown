import {TuringTownHostTutorial} from './scenes/host/Tutorial.js';
import {TuringTownHostOnboarding} from './scenes/host/Onboarding.js';
import {TuringTownHostTransition} from './scenes/host/Transition.js';
import {TuringTownHostRapidFire} from './scenes/host/RapidFire.js';
import {TuringTownHostHotTopic} from './scenes/host/HotTopic.js';
import {TuringTownHostDiscussion} from './scenes/host/Discussion.js';
import {TuringTownHostVoting} from './scenes/host/Voting.js';
import {TuringTownHostReveal} from './scenes/host/Reveal.js';
import {TuringTownHostResults} from './scenes/host/Results.js';
import {TuringTownPlayerTutorial} from './scenes/player/Tutorial.js';
import {TuringTownPlayerOnboarding} from './scenes/player/Onboarding.js';
import {TuringTownPlayerTransition} from './scenes/player/Transition.js';
import {TuringTownPlayerRapidFire} from './scenes/player/RapidFire.js';
import {TuringTownPlayerHotTopic} from './scenes/player/HotTopic.js';
import {TuringTownPlayerDiscussion} from './scenes/player/Discussion.js';
import {TuringTownPlayerVoting} from './scenes/player/Voting.js';
import {TuringTownPlayerReveal} from './scenes/player/Reveal.js';
import {TuringTownPlayerResults} from './scenes/player/Results.js';

// Every scene of the game. Their keys follow `<GameId><Role><Stage>` (see core/director.js), which is how the
// director finds the screen for each stage the server announces. main.js spreads this list into the Phaser config.
export const TURING_TOWN_SCENES = [
    TuringTownHostTutorial,
    TuringTownHostOnboarding,
    TuringTownHostTransition,
    TuringTownHostRapidFire,
    TuringTownHostHotTopic,
    TuringTownHostDiscussion,
    TuringTownHostVoting,
    TuringTownHostReveal,
    TuringTownHostResults,
    TuringTownPlayerTutorial,
    TuringTownPlayerOnboarding,
    TuringTownPlayerTransition,
    TuringTownPlayerRapidFire,
    TuringTownPlayerHotTopic,
    TuringTownPlayerDiscussion,
    TuringTownPlayerVoting,
    TuringTownPlayerReveal,
    TuringTownPlayerResults,
];
