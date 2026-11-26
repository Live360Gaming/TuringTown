import {NightScreen} from '../../../game-night/scenes/player/NightScreen.js';

// The Shout Out phone screens that only point at the TV and say how you did.

// After a question: how your buzz went, or a pointer to the TV.
export class ShoutOutPlayerAnswerReveal extends NightScreen {
    constructor() {
        super('ShoutOutPlayerAnswerReveal');
    }

    lines({attempts = [], options = [], correctId}) {
        const right = options.find(o => o.id === correctId)?.text ?? '';
        const mine = attempts.find(a => a.playerId === this.session.you?.id);
        if (!mine) return {title: 'The answer', body: right, detail: 'Look at the TV for the scores.'};
        return mine.correct
            ? {title: 'Correct!', body: right, detail: `+${mine.points} points`}
            : {title: mine.optionId ? 'Not quite' : 'Too slow', body: `It was: ${right}`, detail: `${mine.points} points`};
    }
}

// The end of Shout Out: your points and place. The leader moves the night on.
export class ShoutOutPlayerResults extends NightScreen {
    constructor() {
        super('ShoutOutPlayerResults');
        this.continueLabel = 'Continue';
    }

    lines({standings = []}) {
        const me = this.myPoints(standings);
        return {
            title: 'Shout Out is over',
            body: me ? `You scored ${me.points} points` : '',
            detail: me ? `#${me.rank} of ${me.of}` : '',
        };
    }
}
