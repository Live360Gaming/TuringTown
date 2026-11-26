import {ShoutOutHostTutorial} from './scenes/host/Tutorial.js';
import {ShoutOutHostCategories} from './scenes/host/Categories.js';
import {ShoutOutHostBuzzed, ShoutOutHostQuestion} from './scenes/host/Question.js';
import {ShoutOutHostAnswerReveal} from './scenes/host/AnswerReveal.js';
import {ShoutOutHostResults} from './scenes/host/Results.js';
import {ShoutOutPlayerTutorial} from './scenes/player/Tutorial.js';
import {ShoutOutPlayerCategories} from './scenes/player/Categories.js';
import {ShoutOutPlayerBuzzed, ShoutOutPlayerQuestion} from './scenes/player/Question.js';
import {ShoutOutPlayerAnswerReveal, ShoutOutPlayerResults} from './scenes/player/screens.js';

// Every Shout Out scene. Their keys follow `<GameId><Role><Stage>` (see core/director.js); main.js spreads this list
// into the Phaser config.
export const SHOUT_OUT_SCENES = [
    ShoutOutHostTutorial,
    ShoutOutHostCategories,
    ShoutOutHostQuestion,
    ShoutOutHostBuzzed,
    ShoutOutHostAnswerReveal,
    ShoutOutHostResults,
    ShoutOutPlayerTutorial,
    ShoutOutPlayerCategories,
    ShoutOutPlayerQuestion,
    ShoutOutPlayerBuzzed,
    ShoutOutPlayerAnswerReveal,
    ShoutOutPlayerResults,
];
