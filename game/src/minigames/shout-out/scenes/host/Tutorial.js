import {HOST_HEIGHT, HOST_WIDTH} from '../../../../constants.js';
import {HostStage} from '../../../../core/stage/HostStage.js';
import {TutorialPlayer} from '../../../../core/tutorial/TutorialPlayer.js';
import {createTextButton} from '../../../../core/ui/Button.js';
import {hubErrorMessage} from '../../../../core/gameHub.js';
import {COLORS, textStyle} from '../../../../core/theme.js';
import {TUTORIAL_STEPS} from '../../data/tutorial.js';

// How to play Shout Out, on the TV. The host reports each step so the phones follow along, and says when it is over.
export class ShoutOutHostTutorial extends HostStage {
    constructor() {
        super('ShoutOutHostTutorial');
    }

    build() {
        this.setTitle('Shout Out');
        this.timerText.setVisible(false);

        this.tutorial = new TutorialPlayer(this, TUTORIAL_STEPS, {
            onStep: step => this.report('TutorialStep', step),
            onDone: () => this.report('TutorialDone'),
        });
        this.tutorial.start(0);

        createTextButton(this, HOST_WIDTH - 130, HOST_HEIGHT - 50, 'Skip intro', () => this.tutorial.finish(), {width: 200, height: 56, fontSize: '24px'});
        this.add.text(32, HOST_HEIGHT - 50, 'The first player can skip from their phone', textStyle('meta', {fontSize: '18px', color: COLORS.textDim}))
            .setOrigin(0, 0.5);
        this.events.once('shutdown', () => this.tutorial.destroy());
    }

    report(method, ...args) {
        this.session.invoke(method, ...args).catch(err => console.warn(hubErrorMessage(err)));
    }
}
