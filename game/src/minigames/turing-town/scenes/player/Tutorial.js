import {PlayerStage} from '../../../../core/stage/PlayerStage.js';
import {hubErrorMessage} from '../../../../core/gameHub.js';
import {createTextButton} from '../../../../core/ui/Button.js';
import {COLORS} from '../../../../core/theme.js';
import {TUTORIAL_STEPS} from '../../data/tutorial.js';

// The how-to-play plays on the TV. The phone points there and shows the current step's title and caption.
// The leader (the first player to join) can skip it for everyone.
export class TuringTownPlayerTutorial extends PlayerStage {
    constructor() {
        super('TuringTownPlayerTutorial');
    }

    build() {
        this.timerText.setVisible(false);
        this.title = this.addText('Watch the TV', {fontStyle: 'bold'});
        this.stepTitle = this.addText('', {fontStyle: 'bold', align: 'center'}).setColor(COLORS.gold);
        this.captionText = this.addText('', {align: 'center'});

        this.skipBtn = createTextButton(this, 0, 0, 'Skip intro', () => this.skip(), {width: 280, height: 64, fontSize: '28px', variant: 'primary'});
        this.skipHint = this.addText('The first player can skip this.', {align: 'center'}).setColor(COLORS.textDim);
        this.errorText = this.addText('', {align: 'center'}).setColor(COLORS.danger);

        // The host reports each step it shows, and the server passes it on in the state.
        this.listen(this.session, 'state', () => this.showStep());
        this.listen(this.session, 'roster', () => this.showLeader());
        this.showStep();
        this.showLeader();
    }

    showStep() {
        const step = TUTORIAL_STEPS[this.session.state.step];
        this.stepTitle.setText(step?.title ?? '');
        this.captionText.setText(step?.caption ?? '');
    }

    showLeader() {
        const leader = !!this.session.you?.leader;
        this.skipBtn.setVisible(leader);
        this.skipHint.setVisible(!leader);
    }

    skip() {
        this.skipBtn.setEnabled(false);
        this.errorText.setText('');
        this.session.invoke('TutorialDone').catch(err => {
            this.skipBtn.setEnabled(true);
            this.errorText.setText(hubErrorMessage(err));
        });
    }

    layout(m) {
        const wrap = m.panelW - 80 * m.s;
        this.title.setPosition(m.cx, m.cy - 170 * m.s).setFontSize(m.fs(48, 22));
        this.stepTitle.setPosition(m.cx, m.cy - 70 * m.s).setFontSize(m.fs(34, 18)).setWordWrapWidth(wrap);
        this.captionText.setPosition(m.cx, m.cy + 40 * m.s).setFontSize(m.fs(26, 16)).setWordWrapWidth(wrap);
        this.skipBtn.setPosition(m.cx, m.cy + 190 * m.s);
        this.skipHint.setPosition(m.cx, m.cy + 190 * m.s).setFontSize(m.fs(20, 14)).setWordWrapWidth(wrap);
        this.errorText.setPosition(m.cx, m.cy + 250 * m.s).setFontSize(m.fs(18, 14)).setWordWrapWidth(wrap);
    }
}
