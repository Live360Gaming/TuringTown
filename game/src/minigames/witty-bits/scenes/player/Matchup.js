import {PlayerStage} from '../../../../core/stage/PlayerStage.js';
import {hubErrorMessage} from '../../../../core/gameHub.js';
import {addHoverFill, BTN} from '../../../../core/ui/Button.js';
import {COLORS} from '../../../../core/theme.js';
import {myAnswers} from './Writing.js';

const CARD_W = 500;
const SELECTED = 0x39ff14;

// Vote for the funnier answer. You can change your vote until time is up. Authors sit their own matchup out.
export class WittyBitsPlayerMatchup extends PlayerStage {
    constructor() {
        super('WittyBitsPlayerMatchup');
    }

    build() {
        const {lastBit, prompt, answers = [], authors = []} = this.session.state.data ?? {};
        this.promptText = this.addText(prompt ?? '', {fontStyle: 'bold', align: 'center'}).setColor(COLORS.gold);
        this.statusText = this.addText('', {align: 'center'});
        this.cards = [];

        const me = this.session.you?.id;
        if (!lastBit && authors.includes(me)) {
            this.statusText.setText('This one is yours!\nSit tight while the others vote.');
            return;
        }

        const mine = myAnswers(this.session);
        this.statusText.setText(lastBit ? 'Vote for your favorite' : 'Tap the funnier answer');
        answers.filter(a => !a.missing).forEach(answer => this.cards.push(this.createCard(answer, lastBit && mine.has(answer.text))));
    }

    createCard(answer, own) {
        const label = this.add.text(0, 0, own ? `${answer.text}\n(yours)` : answer.text, {
            fontSize: '24px', color: own ? COLORS.textDim : '#ffffff', align: 'center', wordWrap: {width: CARD_W - 40},
        }).setOrigin(0.5);
        const height = Math.max(90, label.height + 36);
        const bg = this.add.rectangle(0, 0, CARD_W, height, BTN).setStrokeStyle(3, 0xffffff);
        const container = this.add.container(0, 0, [bg, label]);
        container.cardHeight = height;

        if (!own) {
            bg.setInteractive({useHandCursor: true});
            addHoverFill(bg);
            bg.on('pointerup', () => this.vote(answer, bg));
        } else {
            bg.setAlpha(0.5);
        }
        return {container, bg};
    }

    async vote(answer, bg) {
        try {
            await this.session.invoke('CastWittyVote', answer.id);
            this.cards.forEach(card => card.bg.setStrokeStyle(3, 0xffffff));
            bg.setStrokeStyle(6, SELECTED);
            this.statusText.setText('Vote in! You can change it until time is up.');
        } catch (err) {
            this.statusText.setText(hubErrorMessage(err));
        }
    }

    layout(m) {
        const wrap = m.panelW - 50 * m.s;
        this.promptText.setPosition(m.cx, m.top + 150 * m.s).setFontSize(m.fs(28, 18)).setWordWrapWidth(wrap);
        const statusY = this.promptText.y + this.promptText.height / 2 + 40 * m.s;
        this.statusText.setPosition(m.cx, this.cards.length ? statusY : m.cy).setFontSize(m.fs(22, 16)).setWordWrapWidth(wrap);

        const scale = Math.min(m.s, (m.panelW - 30) / CARD_W);
        let y = statusY + 40 * m.s;
        this.cards.forEach(({container}) => {
            const h = container.cardHeight * scale;
            container.setScale(scale).setPosition(m.cx, y + h / 2);
            y += h + 14 * m.s;
        });
    }
}
