import {playTrack, trackFor as turingTown} from './turing-town/music.js';
import {trackFor as wittyBits} from './witty-bits/music.js';
import {trackFor as gameNight} from './game-night/music.js';

// Music that follows the stage, for whichever game the state belongs to. Each game says which track goes with each of
// its stages (its own music.js); the tracks themselves are loaded by the Preloader and Host (minigames/turing-town/music.js).

const TRACK_FOR = {
    'turing-town': turingTown,
    'witty-bits': wittyBits,
    'game-night': gameNight,
};

/** The track for a server state, or null for silence. */
export function trackForState(state) {
    return (TRACK_FOR[state?.gameId] ?? gameNight)(state);
}

/** Music that follows the session's stage. Returns a function that stops following. */
export function followStageMusic(game, session) {
    // The player ignores a request for the track already playing, so this is safe on every state.
    const onState = state => playTrack(game, trackForState(state));
    session.on('state', onState);
    if (session.state) onState(session.state);
    return () => session.off('state', onState);
}
