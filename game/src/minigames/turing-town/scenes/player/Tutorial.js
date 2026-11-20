import {PlayerStage} from '../../../../core/stage/PlayerStage.js';
import {TUTORIAL_STEPS} from '../../data/tutorial.js';

// The how-to-play plays on the TV. The phone just points there and shows the caption of the current step.
export class TuringTownPlayerTutorial extends PlayerStage {
    constructor() {
        super('TuringTownPlayerTutorial');
    }

    build() {
        this.timerText.setVisible(false);
        this.title = this.addText('Watch the TV', {fontStyle: 'bold'});
        this.captionText = this.addText('', {align: 'center'});

        // The host reports each step it shows, and the server passes it on in the state.
        this.listen(this.session, 'state', () => this.showCaption());
        this.showCaption();
    }

    showCaption() {
        this.captionText.setText(TUTORIAL_STEPS[this.session.state.step]?.caption ?? '');
    }

    layout(m) {
        this.title.setPosition(m.cx, m.cy - 100 * m.s).setFontSize(52 * m.s);
        this.captionText.setPosition(m.cx, m.cy + 50 * m.s).setFontSize(30 * m.s).setWordWrapWidth(m.panelW - 80 * m.s);
    }
}
