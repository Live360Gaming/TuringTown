// Which track goes with each of the game night's own stages (it borrows Turing Town's tracks). Played on the TV only.

/** The track key for a game-night state, or null for silence. */
export function trackFor(state) {
    switch (state?.stage) {
        case 'Lobby':
        case 'Planning':
        case 'Scoreboard':
            return 'tt_music_lobby';
        case 'Finale':
            // The tracks are written from the humans' side.
            return state.data?.winner === 'AI' ? 'tt_music_defeat' : 'tt_music_victory';
        default:
            return null;
    }
}
