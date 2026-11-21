import {addHoverFill, BTN} from '../../../../core/ui/Button.js';
import {createAvatar} from '../../../../core/characters.js';
import {hubErrorMessage} from '../../../../core/gameHub.js';
import {readableOn} from '../../../../core/theme.js';
import {PlayerStage} from '../../../../core/stage/PlayerStage.js';

const CARD_W = 240;
const CARD_H = 100;
const SELECTED = 0x39ff14;

// Tap the player you think is a bot. You can change your vote until time is up.
export class TuringTownPlayerVoting extends PlayerStage {
    constructor() {
        super('TuringTownPlayerVoting');
    }

    build() {
        this.title = this.addText(`Round ${this.session.state.round}: Vote`, {fontStyle: 'bold'});
        this.statusText = this.addText('', {align: 'center'});
        this.cards = [];

        if (this.session.spectating) {
            this.statusText.setText('You were voted out. You can only watch now.');
            return;
        }

        this.statusText.setText('Tap who you think is a bot');
        const me = this.session.you;
        this.session.roster
            .filter(player => player.alive && player.id !== me.id)
            .forEach(player => this.cards.push(this.createCard(player)));
    }

    createCard(player) {
        const bg = this.add.rectangle(0, 0, CARD_W, CARD_H, BTN)
            .setStrokeStyle(3, 0xffffff)
            .setInteractive({useHandCursor: true});
        const avatar = createAvatar(this, -CARD_W / 2 + 50, 0, 64, player);
        const label = this.add.text(-CARD_W / 2 + 96, 0, player.alias, {fontSize: '22px', color: readableOn(player.color, '#405FC2'), wordWrap: {width: CARD_W - 110}})
            .setOrigin(0, 0.5);
        const container = this.add.container(0, 0, [bg, avatar, label]);

        addHoverFill(bg);
        bg.on('pointerup', () => this.vote(player, bg));

        return {container, bg};
    }

    async vote(player, bg) {
        try {
            await this.session.invoke('CastVote', player.id);
            this.cards.forEach(card => card.bg.setStrokeStyle(3, 0xffffff));
            bg.setStrokeStyle(6, SELECTED);
            this.statusText.setText(`You voted for ${player.alias}.\nYou can change it until time is up.`);
        } catch (err) {
            this.statusText.setText(hubErrorMessage(err));
        }
    }

    layout(m) {
        this.title.setPosition(m.cx, m.top + 120 * m.s).setFontSize(m.fs(36, 22));
        this.statusText.setPosition(m.cx, m.top + 175 * m.s).setFontSize(m.fs(22, 16)).setWordWrapWidth(m.panelW - 50 * m.s);

        // Two columns of cards.
        const colGap = 16 * m.s;
        const rowGap = 14 * m.s;
        const cardW = CARD_W * m.s;
        const cardH = CARD_H * m.s;
        const firstY = m.top + 270 * m.s;
        this.cards.forEach(({container}, i) => {
            const col = i % 2;
            const row = Math.floor(i / 2);
            container.setScale(m.s).setPosition(
                m.cx + (col === 0 ? -1 : 1) * (cardW / 2 + colGap / 2),
                firstY + row * (cardH + rowGap),
            );
        });
    }
}
