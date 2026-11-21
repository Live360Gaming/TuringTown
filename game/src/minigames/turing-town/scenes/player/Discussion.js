import {PlayerStage} from '../../../../core/stage/PlayerStage.js';
import {addHoverFill, BTN, createTextButton} from '../../../../core/ui/Button.js';
import * as Phaser from 'phaser';
import {ChatFeed} from '../../../../core/ui/ChatFeed.js';
import {hubErrorMessage} from '../../../../core/gameHub.js';

const CHIP_W = 240;
const CHIP_H = 40;

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

        // Lines that @mention you are shown in gold.
        this.feed = new ChatFeed(this, {highlightPlayerId: this.session.you?.id ?? null});
        this.feed.setMessages(this.session.transcript);
        this.listen(this.session, 'chat', message => {
            this.feed.add(message);
            this.notifyIfMentioned(message);
        });
        this.events.once('shutdown', () => this.feed.destroy());

        this.chips = [];

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

            // One tap adds "@Alias " to what you are typing, so nobody has to type a full name on a phone.
            this.chips = this.session.roster
                .filter(player => player.alive && player.id !== this.session.you.id)
                .map(player => this.createChip(player));
        }
    }

    createChip(player) {
        const bg = this.add.rectangle(0, 0, CHIP_W, CHIP_H, BTN)
            .setStrokeStyle(2, Phaser.Display.Color.HexStringToColor(player.color).color)
            .setInteractive({useHandCursor: true});
        const label = this.add.text(0, 0, `@${player.alias}`, {fontSize: '18px', color: player.color}).setOrigin(0.5);
        const chip = this.add.container(0, 0, [bg, label]);

        addHoverFill(bg);
        bg.on('pointerup', () => this.insertMention(player.alias));
        return chip;
    }

    /** Puts "@Alias " where the cursor is in the chat box. */
    insertMention(alias) {
        const input = this.chatInput.node;
        const text = `@${alias} `;
        const start = input.selectionStart ?? input.value.length;
        const end = input.selectionEnd ?? start;
        input.setRangeText(text, start, end, 'end');
        input.focus();
    }

    /** Someone @mentioned you: say so, and buzz the phone if it can. */
    notifyIfMentioned(message) {
        const me = this.session.you;
        if (!me || message.playerId === me.id || !message.mentions?.includes(me.id)) return;

        navigator.vibrate?.(200);
        this.statusText.setText(`${message.alias} mentioned you`).setColor('#ffd54a');
        this.time.delayedCall(4000, () => this.statusText.setText('').setColor('#ffffff'));
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

        // The mention chips sit in two columns just above the chat box.
        const chipGap = 8 * m.s;
        const chipW = (m.panelW - 2 * pad - chipGap) / 2;
        const chipRows = Math.ceil(this.chips.length / 2);
        const chipsTop = inputTop - 14 * m.s - chipRows * (CHIP_H * m.s + chipGap);
        this.chips.forEach((chip, i) => {
            const col = i % 2;
            const row = Math.floor(i / 2);
            chip.setScale(chipW / CHIP_W, m.s);
            chip.setPosition(
                m.left + pad + chipW / 2 + col * (chipW + chipGap),
                chipsTop + CHIP_H * m.s / 2 + row * (CHIP_H * m.s + chipGap),
            );
        });
        const feedBottom = (this.chips.length > 0 ? chipsTop : inputTop - 14 * m.s) - 8 * m.s;

        this.title.setPosition(m.cx, m.top + 120 * m.s).setFontSize(34 * m.s);
        this.statusText.setPosition(m.cx, m.top + 175 * m.s).setFontSize(22 * m.s).setWordWrapWidth(m.panelW - 2 * pad);

        this.feed.resize({
            x: m.left + pad, y: feedTop + 36 * m.s, width: m.panelW - 2 * pad, height: feedBottom - feedTop - 36 * m.s,
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
