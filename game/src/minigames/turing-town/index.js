import {TuringTownHostTutorial} from './scenes/host/Tutorial.js';
import {TuringTownHostOnboarding} from './scenes/host/Onboarding.js';
import {TuringTownHostQuickFire} from './scenes/host/QuickFire.js';
import {TuringTownHostDiscussion} from './scenes/host/Discussion.js';
import {TuringTownHostVoting} from './scenes/host/Voting.js';
import {TuringTownHostReveal} from './scenes/host/Reveal.js';
import {TuringTownHostResults} from './scenes/host/Results.js';
import {TuringTownPlayerTutorial} from './scenes/player/Tutorial.js';
import {TuringTownPlayerOnboarding} from './scenes/player/Onboarding.js';
import {TuringTownPlayerQuickFire} from './scenes/player/QuickFire.js';
import {TuringTownPlayerDiscussion} from './scenes/player/Discussion.js';
import {TuringTownPlayerVoting} from './scenes/player/Voting.js';
import {TuringTownPlayerReveal} from './scenes/player/Reveal.js';
import {TuringTownPlayerResults} from './scenes/player/Results.js';

// Every scene of the game. Their keys follow `<GameId><Role><Stage>` (see core/director.js), which is how the
// director finds the screen for each stage the server announces. main.js spreads this list into the Phaser config.
export const TURING_TOWN_SCENES = [
    TuringTownHostTutorial,
    TuringTownHostOnboarding,
    TuringTownHostQuickFire,
    TuringTownHostDiscussion,
    TuringTownHostVoting,
    TuringTownHostReveal,
    TuringTownHostResults,
    TuringTownPlayerTutorial,
    TuringTownPlayerOnboarding,
    TuringTownPlayerQuickFire,
    TuringTownPlayerDiscussion,
    TuringTownPlayerVoting,
    TuringTownPlayerReveal,
    TuringTownPlayerResults,
];
