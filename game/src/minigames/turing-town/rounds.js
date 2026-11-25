// The words for each round type, and the suspicion meter as one line. Shared by the TV and the phones.
// `data` is the server's round data: {kind: 'QuickFire' | 'HotSeat' | 'Final', prompt, hotSeat, meter}.

const NAMES = {QuickFire: 'Quick Fire', HotSeat: 'Hot Seat', Final: 'Final'};

export const roundTitle = state => `Round ${state.round}: ${NAMES[state.data?.kind] ?? 'Discuss'}`;

/** What to do during this round's chat. */
export function chatHint(data) {
    if (data?.kind === 'QuickFire') return data.prompt;
    if (data?.kind === 'HotSeat' && data.hotSeat) return `${data.hotSeat.alias} is on the hot seat. Question them!`;
    if (data?.kind === 'Final') return 'Last chance to talk. The next vote decides it.';
    return '';
}

/** "Suspicion: Red Apple 50%  Blue Plum 25%", or '' before anyone has voted. */
export function meterLine(meter = []) {
    const suspects = meter.filter(level => level.votes > 0).slice(0, 4);
    return suspects.length ? `Suspicion: ${suspects.map(level => `${level.alias} ${level.percent}%`).join('   ')}` : '';
}
