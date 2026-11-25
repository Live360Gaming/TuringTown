import {createTextButton} from '../../../../core/ui/Button.js';
import {hubErrorMessage} from '../../../../core/gameHub.js';
import {PlayerStage} from '../../../../core/stage/PlayerStage.js';

// One short answer to the hot topic. It stays hidden unless it is the one shown on the hot seat.
export class TuringTownPlayerHotTopic extends PlayerStage {
    constructor() {
        super('TuringTownPlayerHotTopic', {escapeLeaves: false});
    }

    build() {
        this.sending = false;
        this.title = this.addText('One more question', {fontStyle: 'bold'});
        this.promptText = this.addText(this.session.state.data?.topic ?? '', {align: 'center'});
        this.statusText = this.addText('Nobody sees this yet. It may come back.', {align: 'center'});

        // A real HTML input so phones bring up their keyboard.
        const input = document.createElement('input');
        input.placeholder = 'Your answer';
        input.className = 'tt-input';
        input.maxLength = 200;
        input.autocomplete = 'off';
        input.addEventListener('keydown', e => {
            if (e.key === 'Enter') this.send();
        });
        this.inputEl = input;
        this.answerInput = this.add.dom(0, 0, input);
        this.sendBtn = createTextButton(this, 0, 0, 'Send', () => this.send(), {width: 160, height: 56, fontSize: '26px'});
    }

    async send() {
        const text = this.inputEl.value.trim();
        if (!text || this.sending) return;
        this.sending = true;
        try {
            await this.session.invoke('SubmitHotTopic', text);
            this.statusText.setText('Locked in. You can change it until time is up.');
        } catch (err) {
            this.statusText.setText(hubErrorMessage(err));
        } finally {
            this.sending = false;
        }
    }

    layout(m) {
        const pad = 24 * m.s;
        const wrap = m.panelW - 2 * pad;
        this.title.setPosition(m.cx, m.top + 120 * m.s).setFontSize(m.fs(36, 22));
        this.promptText.setPosition(m.cx, m.top + 220 * m.s).setFontSize(m.fs(30, 18)).setWordWrapWidth(wrap);
        this.inputEl.style.width = `${wrap}px`;
        this.inputEl.style.fontSize = `${Math.max(16, 24 * m.s)}px`;
        this.answerInput.setPosition(m.cx, m.top + 330 * m.s).updateSize();
        this.sendBtn.setPosition(m.cx, m.top + 420 * m.s).setScale(Math.max(m.s, 0.8));
        this.statusText.setPosition(m.cx, m.top + 500 * m.s).setFontSize(m.fs(22, 16)).setWordWrapWidth(wrap);
    }
}
