import {PlayerStage} from '../../../../core/stage/PlayerStage.js';
import {hubErrorMessage} from '../../../../core/gameHub.js';
import {addHoverFill, BTN} from '../../../../core/ui/Button.js';
import {COLORS} from '../../../../core/theme.js';

const CARD_W = 460;
const CARD_H = 74;
const SELECTED = 0x39ff14;

// Pick up to two categories. Each tap toggles one; the picks are sent as they change.
export class ShoutOutPlayerCategories extends PlayerStage {
    constructor() {
        super('ShoutOutPlayerCategories');
    }

    build() {
        const {categories = [], maxPicks = 2} = this.session.state.data ?? {};
        this.maxPicks = maxPicks;
        this.picked = [];
        this.title = this.addText(`Pick up to ${maxPicks}`, {fontStyle: 'bold'}).setColor(COLORS.gold);
        this.statusText = this.addText('', {align: 'center'});
        this.cards = categories.map(category => this.createCard(category));
    }

    createCard(category) {
        const bg = this.add.rectangle(0, 0, CARD_W, CARD_H, BTN).setStrokeStyle(3, 0xffffff).setInteractive({useHandCursor: true});
        addHoverFill(bg);
        const label = this.add.text(0, 0, category.name, {fontSize: '28px', color: '#ffffff', align: 'center'}).setOrigin(0.5);
        const container = this.add.container(0, 0, [bg, label]);
        bg.on('pointerup', () => this.toggle(category.id));
        return {id: category.id, bg, container};
    }

    toggle(id) {
        if (this.picked.includes(id)) this.picked = this.picked.filter(p => p !== id);
        else {
            this.picked.push(id);
            if (this.picked.length > this.maxPicks) this.picked.shift();
        }
        this.cards.forEach(card => card.bg.setStrokeStyle(this.picked.includes(card.id) ? 6 : 3, this.picked.includes(card.id) ? SELECTED : 0xffffff));
        if (this.picked.length === 0) {
            this.statusText.setText('Pick at least one.');
            return;
        }
        this.session.invoke('ShoutOutCategories', this.picked)
            .then(() => this.statusText.setText('Got it! You can change your picks until time is up.'))
            .catch(err => this.statusText.setText(hubErrorMessage(err)));
    }

    layout(m) {
        const wrap = m.panelW - 50 * m.s;
        this.title.setPosition(m.cx, m.top + m.headerH + 50 * m.s).setFontSize(m.fs(34, 20));
        const scale = Math.min(m.s, (m.panelW - 30) / CARD_W);
        const gap = 14 * m.s;
        let y = this.title.y + 50 * m.s;
        this.cards.forEach(({container}) => {
            const h = CARD_H * scale;
            container.setScale(scale).setPosition(m.cx, y + h / 2);
            y += h + gap;
        });
        this.statusText.setPosition(m.cx, y + 30 * m.s).setFontSize(m.fs(20, 14)).setWordWrapWidth(wrap);
    }
}
