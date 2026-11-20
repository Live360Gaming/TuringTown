import {PlayerStage} from '../../../../core/stage/PlayerStage.js';

// The end of the game, on the phone. The host decides when everyone goes back to the lobby.
export class TuringTownPlayerResults extends PlayerStage {
    constructor() {
        super('TuringTownPlayerResults');
    }

    build() {
        this.timerText.setVisible(false);

        const {winner} = this.session.state.data ?? {};
        const humansWon = winner === 'Humans';
        this.title = this.addText(humansWon ? 'The humans win!' : 'The bots win!', {fontStyle: 'bold', align: 'center'});
        this.subText = this.addText(humansWon ? 'Every bot was voted out.' : 'The bots blended in perfectly.', {align: 'center'});
        this.hintText = this.addText('Look at the TV to see who was who.', {align: 'center'});
    }

    layout(m) {
        const wrap = m.panelW - 60 * m.s;
        this.title.setPosition(m.cx, m.cy - 80 * m.s).setFontSize(48 * m.s).setWordWrapWidth(wrap);
        this.subText.setPosition(m.cx, m.cy + 20 * m.s).setFontSize(28 * m.s).setWordWrapWidth(wrap);
        this.hintText.setPosition(m.cx, m.cy + 120 * m.s).setFontSize(24 * m.s).setWordWrapWidth(wrap);
    }
}
