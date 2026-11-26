import {PlayerStage} from '../../../../core/stage/PlayerStage.js';
import {hubErrorMessage} from '../../../../core/gameHub.js';
import {addHoverFill, BTN} from '../../../../core/ui/Button.js';
import {COLORS} from '../../../../core/theme.js';

const BUZZ_READY = 0xff2d6f;
const BUZZ_WAIT = 0x55597d;

// The buzzer. Grey while the room reads the question, red once the answers are up. You cannot buzz again on a question
// you already got wrong.
export class ShoutOutPlayerQuestion extends PlayerStage {
    constructor() {
        super('ShoutOutPlayerQuestion');
    }

    build() {
        const data = this.session.state.data ?? {};
        this.out = (data.out ?? []).includes(this.session.you?.id);
        this.questionText = this.addText(data.text ?? '', {fontStyle: 'bold', align: 'center'}).setColor(COLORS.gold);
        this.statusText = this.addText('', {align: 'center'});

        this.button = this.add.circle(0, 0, 150, BUZZ_WAIT).setStrokeStyle(8, 0xffffff).setInteractive({useHandCursor: true});
        this.buttonLabel = this.add.text(0, 0, 'BUZZ', {fontSize: '64px', fontStyle: 'bold', color: '#ffffff'}).setOrigin(0.5);
        this.button.on('pointerdown', () => this.buzz());
        this.input.keyboard.on('keydown-SPACE', () => this.buzz());

        if (this.out) {
            this.button.disableInteractive().setAlpha(0.35);
            this.buttonLabel.setAlpha(0.35);
            this.statusText.setText('You had your go on this one.');
        }

        this.time.addEvent({delay: 100, loop: true, callback: () => this.tick()});
        this.tick();
    }

    tick() {
        if (this.out || this.sent) return;
        const open = this.session.serverNow() >= (this.session.state.data?.buzzOpensAtUtcMs ?? 0);
        this.button.setFillStyle(open ? BUZZ_READY : BUZZ_WAIT);
        if (!this.locked) this.statusText.setText(open ? 'Know it? BUZZ!' : 'Read the question...');
    }

    buzz() {
        if (this.out || this.sent) return;
        this.sent = true;
        navigator.vibrate?.(60);
        this.session.invoke('Buzz')
            .then(() => this.statusText.setText('You buzzed first!'))
            .catch(err => {
                this.sent = false;
                this.locked = true;
                this.statusText.setText(hubErrorMessage(err));
                this.time.delayedCall(1000, () => { this.locked = false; });
            });
    }

    layout(m) {
        const wrap = m.panelW - 50 * m.s;
        this.questionText.setPosition(m.cx, m.top + m.headerH + 80 * m.s).setFontSize(m.fs(26, 16)).setWordWrapWidth(wrap);
        const radius = Math.min(m.panelW * 0.36, (m.bottom - m.top) * 0.22);
        const y = m.cy + 70 * m.s;
        // Scaled rather than resized, so the tap area grows with it.
        this.button.setPosition(m.cx, y).setScale(radius / 150);
        this.buttonLabel.setPosition(m.cx, y).setFontSize(Math.max(28, radius * 0.42));
        this.statusText.setPosition(m.cx, y + radius + 40 * m.s).setFontSize(m.fs(24, 16)).setWordWrapWidth(wrap);
    }
}

// Someone buzzed. The buzzer gets the four answers; everyone else waits.
export class ShoutOutPlayerBuzzed extends PlayerStage {
    constructor() {
        super('ShoutOutPlayerBuzzed');
    }

    build() {
        const data = this.session.state.data ?? {};
        this.mine = data.buzzerId === this.session.you?.id;
        this.questionText = this.addText(data.text ?? '', {fontStyle: 'bold', align: 'center'}).setColor(COLORS.gold);
        this.statusText = this.addText(this.mine ? 'Your answer?' : `${data.buzzerAlias ?? 'Someone'} buzzed in!`, {align: 'center'});
        this.cards = this.mine ? (data.options ?? []).map(option => this.createCard(option)) : [];
        if (this.mine) navigator.vibrate?.([40, 40, 40]);
    }

    createCard(option) {
        const bg = this.add.rectangle(0, 0, 480, 84, BTN).setStrokeStyle(3, 0xffffff).setInteractive({useHandCursor: true});
        addHoverFill(bg);
        const label = this.add.text(0, 0, `${option.id.toUpperCase()}   ${option.text}`, {
            fontSize: '28px', color: '#ffffff', align: 'center', wordWrap: {width: 440},
        }).setOrigin(0.5);
        const container = this.add.container(0, 0, [bg, label]);
        bg.on('pointerup', () => this.answer(option.id, bg));
        return {container, bg};
    }

    answer(id, bg) {
        if (this.answered) return;
        this.answered = true;
        bg.setStrokeStyle(6, 0x39ff14);
        this.session.invoke('ShoutOutAnswer', id)
            .then(() => this.statusText.setText('Locked in! Look at the TV.'))
            .catch(err => {
                this.answered = false;
                this.statusText.setText(hubErrorMessage(err));
            });
    }

    layout(m) {
        const wrap = m.panelW - 50 * m.s;
        this.questionText.setPosition(m.cx, m.top + m.headerH + 70 * m.s).setFontSize(m.fs(24, 16)).setWordWrapWidth(wrap);
        const statusY = this.questionText.y + this.questionText.height / 2 + 40 * m.s;
        this.statusText.setPosition(m.cx, this.cards.length ? statusY : m.cy).setFontSize(m.fs(28, 18)).setWordWrapWidth(wrap);
        const scale = Math.min(m.s, (m.panelW - 30) / 480);
        let y = statusY + 40 * m.s;
        this.cards.forEach(({container}) => {
            const h = 84 * scale;
            container.setScale(scale).setPosition(m.cx, y + h / 2);
            y += h + 14 * m.s;
        });
    }
}
