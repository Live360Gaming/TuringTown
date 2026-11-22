// Which track goes with each Witty Bits stage. Witty Bits has no music of its own yet, so it borrows Turing Town's
// tracks (see minigames/turing-town/music.js). Played on the TV only.

/** The track key for a Witty Bits state, or null for silence. */
export function trackFor(state) {
    switch (state?.stage) {
        case 'Tutorial':
            return 'tt_music_lobby';
        case 'Writing':
            return 'tt_music_questionnaire';
        case 'Matchup':
        case 'MatchupResult':
            return 'tt_music_chat';
        case 'Results':
            return 'tt_music_victory';
        default:
            return null;
    }
}
