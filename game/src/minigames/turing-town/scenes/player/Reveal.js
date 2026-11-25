import {createAvatar} from '../../../../core/characters.js';
import {readableOn} from '../../../../core/theme.js';
import {PlayerStage} from '../../../../core/stage/PlayerStage.js';
import {meterLine} from '../../rounds.js';

// The result of the vote, on the phone.
export class TuringTownPlayerReveal extends PlayerStage {
    constructor() {
        super('TuringTownPlayerReveal');
    }

    build() {
        // The screen object is reused, so the last reveal's avatar (now destroyed) must be forgotten.
        this.avatar = null;
        const {eliminated, wasBot, final, meter} = this.session.state.data ?? {};
        this.title = this.addText(`Round ${this.session.state.round}`, {fontStyle: 'bold'});

        if (!final) {
            const mine = meter?.find(level => level.playerId === this.session.you?.id)?.percent ?? 0;
            this.verdictText = this.addText(`You are ${mine}% suspected.\n\n${meterLine(meter).split('   ').join('\n')}`, {align: 'center', fontStyle: 'bold'});
        } else if (eliminated) {
            this.avatar = createAvatar(this, 0, 0, 200, eliminated);
            this.nameText = this.addText(eliminated.alias, {color: readableOn(eliminated.color)});
            this.verdictText = this.addText(wasBot ? 'was an AI BOT!' : 'was a human...', {color: wasBot ? '#39ff14' : '#ff4d4d', fontStyle: 'bold'});
            this.meText = this.addText(eliminated.id === this.session.you?.id ? 'That was you.' : '', {align: 'center'});
        } else {
            this.verdictText = this.addText('No verdict!\nThe bots slip away.', {align: 'center', fontStyle: 'bold'});
        }
    }

    layout(m) {
        this.title.setPosition(m.cx, m.top + 120 * m.s).setFontSize(m.fs(36, 22));

        if (this.avatar) {
            this.avatar.setPosition(m.cx, m.cy - 60 * m.s).setScale(m.s);
            this.nameText.setPosition(m.cx, m.cy + 80 * m.s).setFontSize(m.fs(36, 22));
            this.verdictText.setPosition(m.cx, m.cy + 135 * m.s).setFontSize(m.fs(34, 22));
            this.meText.setPosition(m.cx, m.cy + 195 * m.s).setFontSize(m.fs(26, 16));
        } else {
            this.verdictText.setPosition(m.cx, m.cy).setFontSize(m.fs(30, 18)).setWordWrapWidth(m.panelW - 60 * m.s);
        }
    }
}
