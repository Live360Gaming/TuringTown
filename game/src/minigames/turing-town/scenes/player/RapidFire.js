import {hubErrorMessage} from '../../../../core/gameHub.js';
import {PlayerStage} from '../../../../core/stage/PlayerStage.js';
import {createTextButton} from '../../../../core/ui/Button.js';
import {roundTitle} from '../../rounds.js';

// The five public questions, one at a time. Each answer is sent as it is typed, so nothing is lost when time runs out.
export class TuringTownPlayerRapidFire extends PlayerStage {
    constructor() {
        super('TuringTownPlayerRapidFire', {escapeLeaves: false});
    }

    build() {
        this.questions = this.session.state.data?.questions ?? [];
        this.answers = this.questions.map(() => '');
        this.sent = this.questions.map(() => '');
        this.index = 0;

        this.title = this.addText(roundTitle(this.session.state), {fontStyle: 'bold'});
        this.countText = this.addText('', {align: 'center'});
        this.questionText = this.addText('', {align: 'center'});
        this.statusText = this.addText('Everyone will see these answers.', {align: 'center'});

        const input = document.createElement('input');
        input.placeholder = 'Your answer';
        input.className = 'tt-input';
        input.maxLength = 200;
        input.autocomplete = 'off';
        input.addEventListener('input', () => {
            this.answers[this.index] = input.value;
            this.pending?.remove();
            this.pending = this.time.delayedCall(900, () => this.send(this.index));
        });
        input.addEventListener('keydown', e => {
            if (e.key === 'Enter') this.move(1);
        });
        this.inputEl = input;
        this.answerInput = this.add.dom(0, 0, input);
        this.backBtn = createTextButton(this, 0, 0, 'Back', () => this.move(-1), {width: 130, height: 56, fontSize: '24px', variant: 'ghost'});
        this.nextBtn = createTextButton(this, 0, 0, 'Next', () => this.move(1), {width: 170, height: 56, fontSize: '26px'});
        this.show();
    }

    show() {
        this.countText.setText(`Question ${this.index + 1} of ${this.questions.length}`);
        this.questionText.setText(this.questions[this.index] ?? '');
        this.inputEl.value = this.answers[this.index];
    }

    move(step) {
        this.pending?.remove();
        this.send(this.index);
        const next = this.index + step;
        if (next >= 0 && next < this.questions.length) {
            this.index = next;
            this.show();
        } else if (step > 0) {
            const missing = this.answers.filter(a => !a.trim()).length;
            this.statusText.setText(missing ? `${missing} still blank. Use Back to fill them in.` : 'All five are in. Watch the TV.');
        }
    }

    async send(index) {
        const text = this.answers[index].trim();
        if (!text || text === this.sent[index]) return;
        this.sent[index] = text;
        try {
            await this.session.invoke('SubmitRapidAnswer', `q${index + 1}`, text);
        } catch (err) {
            this.sent[index] = '';
            this.statusText.setText(`Question ${index + 1}: ${hubErrorMessage(err)}`);
        }
    }

    layout(m) {
        const pad = 24 * m.s;
        const wrap = m.panelW - 2 * pad;
        this.title.setPosition(m.cx, m.top + 120 * m.s).setFontSize(m.fs(36, 22));
        this.countText.setPosition(m.cx, m.top + 175 * m.s).setFontSize(m.fs(22, 14));
        this.questionText.setPosition(m.cx, m.top + 250 * m.s).setFontSize(m.fs(30, 18)).setWordWrapWidth(wrap);
        this.inputEl.style.width = `${wrap}px`;
        this.inputEl.style.fontSize = `${Math.max(16, 24 * m.s)}px`;
        this.answerInput.setPosition(m.cx, m.top + 350 * m.s).updateSize();
        this.backBtn.setPosition(m.cx - 100 * m.s, m.top + 440 * m.s).setScale(Math.max(m.s, 0.8));
        this.nextBtn.setPosition(m.cx + 80 * m.s, m.top + 440 * m.s).setScale(Math.max(m.s, 0.8));
        this.statusText.setPosition(m.cx, m.top + 520 * m.s).setFontSize(m.fs(20, 14)).setWordWrapWidth(wrap);
    }
}
