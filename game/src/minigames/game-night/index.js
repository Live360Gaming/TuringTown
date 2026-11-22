import {GameNightHostPlanning} from './scenes/host/Planning.js';
import {GameNightHostScoreboard} from './scenes/host/Scoreboard.js';
import {GameNightHostFinale} from './scenes/host/Finale.js';
import {GameNightPlayerFinale, GameNightPlayerPlanning, GameNightPlayerScoreboard} from './scenes/player/screens.js';

// The game night's own screens: Planning (the AI picks the games), the Scoreboard between games, and the Finale.
// Their keys follow `<GameId><Role><Stage>` with the game id `game-night` (see core/director.js).
export const GAME_NIGHT_SCENES = [
    GameNightHostPlanning,
    GameNightHostScoreboard,
    GameNightHostFinale,
    GameNightPlayerPlanning,
    GameNightPlayerScoreboard,
    GameNightPlayerFinale,
];
