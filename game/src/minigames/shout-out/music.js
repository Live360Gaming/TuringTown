// Which track goes with each Shout Out stage. Shout Out has no music of its own yet, so it borrows Turing Town's
// tracks (see minigames/turing-town/music.js). Played on the TV only.

/** The track key for a Shout Out state, or null for silence. */
export function trackFor(state) {
    switch (state?.stage) {
        case 'Tutorial':
        case 'Categories':
            return 'tt_music_lobby';
        case 'Question':
        case 'Buzzed':
        case 'AnswerReveal':
            return 'tt_music_questionnaire';
        case 'Results':
            return 'tt_music_victory';
        default:
            return null;
    }
}
