import {HOST_CENTER_X, HOST_HEIGHT} from '../../../../constants.js';
import {HostStage} from '../../../../core/stage/HostStage.js';
import {COLORS, HEX, textStyle} from '../../../../core/theme.js';

// The night begins: the AI team picks which games are played and in what order, then shows its choice.
export class GameNightHostPlanning extends HostStage {
    constructor() {
        super('GameNightHostPlanning');
    }

    build() {
        this.setTitle('Game Night');
        this.items = [];
        this.thinking = this.add.text(HOST_CENTER_X, 260, '', textStyle('instruction', {fontSize: '34px'})).setOrigin(0.5);
        this.dots = 0;
        this.time.addEvent({delay: 400, loop: true, callback: () => this.tick()});

        this.createRosterRow(HOST_HEIGHT - 130, {size: 110});
        this.listen(this.session, 'state', () => this.show());
        this.show();
    }

    tick() {
        if (!this.thinking.visible) return;
        this.dots = (this.dots + 1) % 4;
        this.thinking.setText(`The AI is choosing tonight's games${'.'.repeat(this.dots)}`);
    }

    show() {
        const {order = [], line} = this.session.state.data ?? {};
        this.items.forEach(item => item.destroy());
        this.items = [];

        const chosen = order.length > 0;
        this.thinking.setVisible(!chosen);
        this.setInstruction(chosen ? 'Tonight the AI wants to beat you at...' : 'Humans vs AI: whoever scores more tonight wins');
        // The planning time is not worth counting down; the reveal is short.
        this.timerText.setVisible(chosen);
        if (!chosen) return;

        order.forEach((game, i) => {
            const y = 210 + i * 90;
            this.items.push(
                this.add.graphics().fillStyle(HEX.bgDeep, 0.88).fillRoundedRect(HOST_CENTER_X - 300, y - 36, 600, 72, 18)
                    .lineStyle(2, HEX.cyan, 0.9).strokeRoundedRect(HOST_CENTER_X - 300, y - 36, 600, 72, 18),
                this.add.text(HOST_CENTER_X - 260, y, `${i + 1}`, textStyle('title', {fontSize: '34px', color: COLORS.gold})).setOrigin(0.5),
                this.add.text(HOST_CENTER_X - 220, y, game.title, textStyle('title', {fontSize: '32px'})).setOrigin(0, 0.5),
            );
        });

        if (line) {
            const y = 210 + order.length * 90 + 20;
            this.items.push(this.add.text(HOST_CENTER_X, y, `"${line}"\n- the AI`, textStyle('bodyOnArt', {fontSize: '26px', align: 'center', color: COLORS.green, wordWrap: {width: 900}}))
                .setOrigin(0.5, 0));
        }
    }
}
