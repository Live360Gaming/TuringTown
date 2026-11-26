import {HOST_CENTER_X, HOST_HEIGHT} from '../../../../constants.js';
import {HostStage} from '../../../../core/stage/HostStage.js';
import {COLORS, textStyle} from '../../../../core/theme.js';
import {drawOptions, drawQuestion, questionTitle} from './board.js';

// The right answer in green, wrong picks in red, and how each buzz went.
export class ShoutOutHostAnswerReveal extends HostStage {
    constructor() {
        super('ShoutOutHostAnswerReveal');
    }

    build() {
        const data = this.session.state.data ?? {};
        const {correctId, attempts = [], leaders = []} = data;
        this.setTitle(questionTitle(data));
        const wrong = new Set(attempts.filter(a => !a.correct && a.optionId).map(a => a.optionId));
        const bottom = drawQuestion(this, data.text ?? '', 180);
        drawOptions(this, data.options ?? [], bottom + 20,
            option => option.id === correctId ? 'right' : wrong.has(option.id) ? 'wrong' : 'dim');

        const winner = attempts.find(a => a.correct);
        const line = winner
            ? `${winner.alias} got it! +${winner.points}`
            : attempts.length ? 'Nobody got it!' : 'Nobody buzzed!';
        this.add.text(HOST_CENTER_X, HOST_HEIGHT - 95, line, textStyle('title', {fontSize: '34px', color: winner ? COLORS.green : COLORS.gold}))
            .setOrigin(0.5);

        const misses = attempts.filter(a => !a.correct).map(a => `${a.alias} ${a.points}`);
        const top = leaders.filter(s => s.points !== 0).map((s, i) => `${i + 1}. ${s.alias} ${s.points}`).join('    ');
        this.add.text(HOST_CENTER_X, HOST_HEIGHT - 55, misses.length ? `Missed: ${misses.join(', ')}` : '',
            textStyle('meta', {fontSize: '20px', color: COLORS.danger})).setOrigin(0.5);
        this.add.text(HOST_CENTER_X, HOST_HEIGHT - 22, top, textStyle('meta', {fontSize: '20px', color: COLORS.textMuted})).setOrigin(0.5);
    }
}
