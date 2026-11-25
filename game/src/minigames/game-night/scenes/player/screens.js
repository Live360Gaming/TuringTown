import {goodbyeText} from '../../quips.js';
import {NightScreen} from './NightScreen.js';

// The night's own stages on the phone. The TV shows the detail; the phone says where things stand.

export class GameNightPlayerPlanning extends NightScreen {
    constructor() {
        super('GameNightPlayerPlanning');
    }

    lines({order = [], line}) {
        if (order.length === 0) return {title: 'Game Night', body: 'The AI is choosing tonight\'s games...', detail: 'Score more than the AI to win.'};
        return {
            title: 'Tonight\'s games',
            body: order.map((game, i) => `${i + 1}. ${game.title}`).join('\n'),
            detail: line ? `"${line}"` : '',
        };
    }
}

export class GameNightPlayerScoreboard extends NightScreen {
    constructor() {
        super('GameNightPlayerScoreboard');
        this.continueLabel = 'Next game';
    }

    lines({next, standings, teams}) {
        const me = this.myPoints(standings);
        return {
            title: 'Scoreboard',
            body: me ? `You have ${me.points} points\n(#${me.rank} of ${me.of})` : '',
            detail: [teams ? `Humans ${teams.humans}  vs  AI ${teams.ai}` : 'Team totals stay secret for now.', next ? `Up next: ${next.title}` : '']
                .filter(Boolean).join('\n'),
        };
    }
}

export class GameNightPlayerFinale extends NightScreen {
    constructor() {
        super('GameNightPlayerFinale');
        this.continueLabel = 'Back to lobby';
    }

    build() {
        super.build();
        this.quipText = this.addText('', {align: 'center'});
        this.quipText.setText(goodbyeText(this.session.state.data?.goodbye));
    }

    layout(m) {
        super.layout(m);
        // Below the lines above; the button and any error move down to make room.
        this.quipText?.setPosition(m.cx, m.cy + 170 * m.s).setFontSize(m.fs(22, 15)).setWordWrapWidth(m.panelW - 60 * m.s);
        this.continueBtn.setY(m.cy + 255 * m.s);
        this.errorText.setY(m.cy + 315 * m.s);
    }

    lines({winner, teams = {humans: 0, ai: 0}, roles = []}) {
        const title = winner === 'Humans' ? 'The humans win the night!' : winner === 'AI' ? 'The AI wins the night!' : 'A perfect tie!';
        const bots = roles.filter(r => r.wasBot).map(r => r.alias);
        return {
            title,
            body: `Humans ${teams.humans}  vs  AI ${teams.ai}`,
            detail: bots.length ? `The AI was: ${bots.join(', ')}` : '',
        };
    }
}
