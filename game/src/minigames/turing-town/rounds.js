// The words for each beat of the game, and the suspicion meter as one line. Shared by the TV and the phones.
// `data` is the server's round data: {kind: 'RapidFire' | 'HotSeat' | 'Final', meter, hotSeat, questions, answers,
// topic, shownAnswer}.

const NAMES = {RapidFire: 'Five Questions', HotSeat: 'Hot Seat', Final: 'The Final'};

export const roundTitle = state => NAMES[state.data?.kind] ?? 'Discuss';

/** What to do during this chat. */
export function chatHint(data) {
    if (data?.kind === 'HotSeat' && data.hotSeat) return `${data.hotSeat.alias} is on the hot seat. Question them!`;
    if (data?.kind === 'Final') return 'Last chance to talk. The next vote decides it.';
    return '';
}

/** The hot seat's question and the answer shown under that player's name, or ''. */
export function hotSeatCard(data) {
    if (data?.kind !== 'HotSeat' || !data.hotSeat) return '';
    return `${data.topic}\n"${data.shownAnswer ?? 'said nothing'}" (probably not theirs)`;
}

/** "Suspicion: Red Apple 50%  Blue Plum 25%", or '' before anyone has voted. */
export function meterLine(meter = []) {
    const suspects = meter.filter(level => level.votes > 0).slice(0, 4);
    return suspects.length ? `Suspicion: ${suspects.map(level => `${level.alias} ${level.percent}%`).join('   ')}` : '';
}
