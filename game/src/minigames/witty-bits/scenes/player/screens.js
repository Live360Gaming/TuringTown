import {PlayerStage} from '../../../../core/stage/PlayerStage.js';
import {hubErrorMessage} from '../../../../core/gameHub.js';
import {createTextButton} from '../../../../core/ui/Button.js';
import {COLORS} from '../../../../core/theme.js';
import {NightScreen} from '../../../game-night/scenes/player/NightScreen.js';
import {TUTORIAL_STEPS} from '../../data/tutorial.js';

// The Witty Bits phone screens that only point at the TV and say how you did.

// The how-to-play plays on the TV; the phone shows the current step, and the leader can skip it.
export class WittyBitsPlayerTutorial extends PlayerStage {
    constructor() {
        super('WittyBitsPlayerTutorial');
    }

    build() {
        this.timerText.setVisible(false);
        this.title = this.addText('Watch the TV', {fontStyle: 'bold'});
        this.stepTitle = this.addText('', {fontStyle: 'bold', align: 'center'}).setColor(COLORS.gold);
        this.captionText = this.addText('', {align: 'center'});
        this.skipBtn = createTextButton(this, 0, 0, 'Skip intro', () => this.skip(), {width: 280, height: 64, fontSize: '28px', variant: 'primary'});
        this.skipHint = this.addText('The first player can skip this.', {align: 'center'}).setColor(COLORS.textDim);
        this.errorText = this.addText('', {align: 'center'}).setColor(COLORS.danger);

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

// After a vote: how your own answer did, or a pointer to the TV.
export class WittyBitsPlayerMatchupResult extends NightScreen {
    constructor() {
        super('WittyBitsPlayerMatchupResult');
    }

    lines({answers = []}) {
        const mine = answers.find(a => a.authorId === this.session.you?.id);
        if (!mine) return {title: 'Results', body: 'Look at the TV to see who wrote what.'};
        if (mine.missing) return {title: 'No answer', body: 'You did not answer this one.', detail: 'Next time!'};
        return {
            title: mine.sweep ? 'Clean sweep!' : mine.votes > 0 ? 'Nice one!' : 'Tough crowd',
            body: `Your answer got ${mine.votes} vote${mine.votes === 1 ? '' : 's'}.`,
            detail: `+${mine.points} points`,
        };
    }
}

// The end of Witty Bits: your points and place. The leader moves the night on.
export class WittyBitsPlayerResults extends NightScreen {
    constructor() {
        super('WittyBitsPlayerResults');
        this.continueLabel = 'Continue';
    }

    lines({standings = []}) {
        const me = this.myPoints(standings);
        return {
            title: 'Witty Bits is over',
            body: me ? `You scored ${me.points} points` : '',
            detail: me ? `#${me.rank} of ${me.of}` : '',
        };
    }
}
