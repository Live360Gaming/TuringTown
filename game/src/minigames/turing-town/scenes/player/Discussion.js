import {PlayerStage} from '../../../../core/stage/PlayerStage.js';
import {createTextButton} from '../../../../core/ui/Button.js';
import {ChatFeed} from '../../../../core/ui/ChatFeed.js';
import {hubErrorMessage} from '../../../../core/gameHub.js';

// The anonymous chat on a phone. Only your alias is ever shown. Someone who has been voted out can read but not write.
export class TuringTownPlayerDiscussion extends PlayerStage {
    constructor() {
        super('TuringTownPlayerDiscussion', {escapeLeaves: false});
    }

    build() {
        this.sending = false;
        this.canWrite = !this.session.spectating;

        this.title = this.addText(`Round ${this.session.state.round}: Discuss`, {fontStyle: 'bold'});
        this.statusText = this.addText(this.canWrite ? '' : 'You were voted out. You can only watch now.', {align: 'center'});

        this.feed = new ChatFeed(this);
        this.feed.setMessages(this.session.transcript);
        this.listen(this.session, 'chat', message => this.feed.add(message));
        this.events.once('shutdown', () => this.feed.destroy());

        if (this.canWrite) {
            // A real HTML input so phones bring up their keyboard.
            this.chatInput = this.add.dom(0, 0, 'input', {
                boxSizing: 'border-box',
                padding: '0.3em',
                borderRadius: '12px',
                border: 'none',
            });
            const input = this.chatInput.node;
            input.placeholder = 'Say something...';
            input.maxLength = 200;
            input.autocomplete = 'off';
            input.addEventListener('keydown', e => {
                if (e.key === 'Enter') this.send();
            });

            this.sendBtn = createTextButton(this, 0, 0, 'Send', () => this.send(), {width: 110, height: 56, fontSize: '26px'});
        }
    }

    async send() {
        const input = this.chatInput.node;
        const text = input.value.trim();
        if (!text || this.sending) return;

        this.sending = true;
        try {
            await this.session.invoke('SendChat', text);
            input.value = '';
            this.statusText.setText('');
        } catch (err) {
            this.statusText.setText(hubErrorMessage(err));
        } finally {
            this.sending = false;
        }
    }

    layout(m) {
        const pad = 24 * m.s;
        const feedTop = m.top + 150 * m.s;
        const inputTop = m.bottom - 90 * m.s;

        this.title.setPosition(m.cx, m.top + 120 * m.s).setFontSize(34 * m.s);
        this.statusText.setPosition(m.cx, inputTop - 24 * m.s).setFontSize(22 * m.s).setWordWrapWidth(m.panelW - 2 * pad);

        this.feed.resize({
            x: m.left + pad, y: feedTop, width: m.panelW - 2 * pad, height: inputTop - 50 * m.s - feedTop,
            fontSize: 20 * m.s, avatarSize: 40 * m.s,
        });

        if (this.canWrite && this.chatInput.active) {
            const sendWidth = 110 * m.s;
            const inputWidth = m.panelW - 3 * pad - sendWidth;
            this.chatInput.setPosition(m.left + pad + inputWidth / 2, inputTop + 30 * m.s);
            this.chatInput.node.style.width = `${inputWidth}px`;
            this.chatInput.node.style.fontSize = `${24 * m.s}px`;
            this.chatInput.updateSize();
            this.sendBtn.setPosition(m.right - pad - sendWidth / 2, inputTop + 30 * m.s).setScale(m.s);
        }
    }
}
