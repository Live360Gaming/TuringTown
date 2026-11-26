// Which recorded line goes with what the server says, and how long a line stays relevant.

/**
 * Lines belong to a part of the game; moving to another part stops them. The verdict's ending line runs on from the
 * Reveal into the Results, where the hot seat truth follows it, so those two share a part.
 */
export function scopeFor(state) {
    if (!state || state.stage === 'Lobby' || state.stage === 'Planning') return 'lobby';
    if (state.stage === 'Tutorial') return 'tutorial';
    if (state.stage === 'Transition') return `transition:${state.round}:${state.data?.beat}`;
    if (state.stage === 'Results' || (state.stage === 'Reveal' && state.data?.final)) return 'ending';
    if (state.stage === 'Finale') return 'finale';
    return `${state.stage}:${state.round}`;
}

/** One saying of a line over a screen: a re-render or a repeated state of the same round does not say it again. */
export const speechOccurrence = (key, round) => `${key}:${round}`;

/** The one ending line for the final Reveal's result, as the server reported it. */
export function endingKey({eliminated, wasBot} = {}) {
    if (!eliminated) return 'no_verdict';
    return wasBot ? 'humans_win' : 'bots_win';
}

/** The hot seat truth line, or null when there was no hot seat answer. */
export function truthKey(wasTheirs) {
    if (wasTheirs === true) return 'truth_theirs';
    if (wasTheirs === false) return 'truth_not_theirs';
    return null;
}
