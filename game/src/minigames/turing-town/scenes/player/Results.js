import {PlayerStage} from '../../../../core/stage/PlayerStage.js';
import {hubErrorMessage} from '../../../../core/gameHub.js';
import {createTextButton} from '../../../../core/ui/Button.js';

// The end of the game, on the phone. The host, or the leader (the first player), decides when the night moves on.
export class TuringTownPlayerResults extends PlayerStage {
    constructor() {
        super('TuringTownPlayerResults');
    }

    build() {
        this.timerText.setVisible(false);

        const {winner, hotSeatWasTheirs, roles = []} = this.session.state.data ?? {};
        const humansWon = winner === 'Humans';
        const imposter = roles.find(r => r.imposter);
        this.title = this.addText(humansWon ? 'The humans win!' : 'The bots win!', {fontStyle: 'bold', align: 'center'});
        this.subText = this.addText([humansWon ? 'A bot was caught.' : 'The bots blended in perfectly.',
            hotSeatWasTheirs === true ? 'The hot seat answer really was theirs.' : hotSeatWasTheirs === false ? 'Somebody else wrote the hot seat answer.' : '',
            imposter?.playerId === this.session.you?.id ? 'You were the Imposter! Your bonus shows on the scoreboard.'
                : imposter ? `${imposter.alias} was the Imposter.` : '',
        ].filter(Boolean).join('\n'), {align: 'center'});
        this.hintText = this.addText('Look at the TV to see who was who.', {align: 'center'});

        // Only the leader (the first player to join) can move the night on to the next game.
        this.errorText = this.addText('', {align: 'center'}).setColor('#ff4d4d');
        this.againBtn = createTextButton(this, 0, 0, 'Continue', () => this.playAgain(), {width: 300, height: 64, fontSize: '30px', variant: 'primary'});
        this.againBtn.setVisible(!!this.session.you?.leader);
    }

    playAgain() {
        this.session.invoke('ContinueNight').catch(err => this.errorText.setText(hubErrorMessage(err)));
    }

    layout(m) {
        const wrap = m.panelW - 60 * m.s;
        this.title.setPosition(m.cx, m.cy - 80 * m.s).setFontSize(m.fs(48, 22)).setWordWrapWidth(wrap);
        this.subText.setPosition(m.cx, m.cy + 20 * m.s).setFontSize(m.fs(28, 16)).setWordWrapWidth(wrap);
        this.hintText.setPosition(m.cx, m.cy + 120 * m.s).setFontSize(m.fs(24, 16)).setWordWrapWidth(wrap);
        this.againBtn.setPosition(m.cx, m.cy + 210 * m.s);
        this.errorText.setPosition(m.cx, m.cy + 275 * m.s).setFontSize(m.fs(18, 14)).setWordWrapWidth(wrap);
    }
}
