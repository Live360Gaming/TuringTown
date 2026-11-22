import {WittyBitsHostTutorial} from './scenes/host/Tutorial.js';
import {WittyBitsHostWriting} from './scenes/host/Writing.js';
import {WittyBitsHostMatchup} from './scenes/host/Matchup.js';
import {WittyBitsHostMatchupResult} from './scenes/host/MatchupResult.js';
import {WittyBitsHostResults} from './scenes/host/Results.js';
import {WittyBitsPlayerWriting} from './scenes/player/Writing.js';
import {WittyBitsPlayerMatchup} from './scenes/player/Matchup.js';
import {WittyBitsPlayerMatchupResult, WittyBitsPlayerResults, WittyBitsPlayerTutorial} from './scenes/player/screens.js';

// Every Witty Bits scene. Their keys follow `<GameId><Role><Stage>` (see core/director.js); main.js spreads this list
// into the Phaser config.
export const WITTY_BITS_SCENES = [
    WittyBitsHostTutorial,
    WittyBitsHostWriting,
    WittyBitsHostMatchup,
    WittyBitsHostMatchupResult,
    WittyBitsHostResults,
    WittyBitsPlayerTutorial,
    WittyBitsPlayerWriting,
    WittyBitsPlayerMatchup,
    WittyBitsPlayerMatchupResult,
    WittyBitsPlayerResults,
];
