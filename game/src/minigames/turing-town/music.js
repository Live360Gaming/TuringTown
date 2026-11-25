import {getMusic} from '../../core/music.js';
import {NARRATION_FILES} from '../../narration/registry.js';

// Turing Town's soundtrack and which track goes with each stage. Played on the TV only (see core/music.js).

/** Audio key -> file (under public/audio), whether it loops, and its level under the master volume. */
export const TRACKS = {
    tt_music_lobby: {file: 'turing-town/music/turingtown_lobby.mp3', loop: true, volume: 0.30},
    tt_music_questionnaire: {file: 'turing-town/music/turingtown_questionnaire.mp3', loop: true, volume: 0.22},
    tt_music_chat: {file: 'turing-town/music/turingtown_chat.mp3', loop: true, volume: 0.18},
    tt_music_victory: {file: 'turing-town/music/turingtown_victory.mp3', loop: false, volume: 0.45},
    tt_music_defeat: {file: 'turing-town/music/turingtown_defeat.mp3', loop: false, volume: 0.40},
};

export const LOBBY_TRACK = 'tt_music_lobby';

/**
 * The track for a Turing Town state, or null for silence. The winner comes from the server's results, never the UI.
 * minigames/stageMusic.js picks the right game's trackFor for each state.
 */
export function trackFor(state) {
    switch (state?.stage) {
        case 'Lobby':
        case 'Tutorial':
            return LOBBY_TRACK;
        case 'Onboarding':
        case 'RapidFire':
        case 'HotTopic':
            return 'tt_music_questionnaire';
        case 'Transition':
        case 'Discussion':
        case 'Voting':
        case 'Reveal':
            return 'tt_music_chat';
        case 'Results': {
            const winner = state.data?.winner;
            if (winner === 'Humans') return 'tt_music_victory';
            if (winner === 'Bots') return 'tt_music_defeat';
            return null;
        }
        default:
            return null;
    }
}

/** Plays `key` with its settings from TRACKS. Result tracks fade in quickly over the outgoing music. */
export function playTrack(game, key) {
    const music = getMusic(game);
    const track = TRACKS[key];
    if (!track) {
        music.stop();
        return;
    }
    music.play(key, {loop: track.loop, volume: track.volume, fadeInMs: track.loop ? undefined : 300});
}

/**
 * Starts loading the tracks the Preloader skipped (it only loads the lobby track, since phones never play music), and
 * the tutorial's announcer lines. Safe to call again: only what is missing is loaded. A file that fails to load is
 * simply not played (the tutorial keeps its visual timing).
 */
export function loadStageMusic(scene) {
    const files = [...Object.entries(TRACKS).map(([key, {file}]) => [key, file]), ...Object.entries(NARRATION_FILES)];
    const missing = files.filter(([key]) => !scene.cache.audio.exists(key));
    if (missing.length === 0) return;
    scene.load.setPath('audio');
    missing.forEach(([key, file]) => scene.load.audio(key, file));
    scene.load.start();
}
