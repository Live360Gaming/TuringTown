import {hubErrorMessage} from '../../../../core/gameHub.js';
import {PlayerStage} from '../../../../core/stage/PlayerStage.js';
import {COLORS} from '../../../../core/theme.js';
import {createTextButton} from '../../../../core/ui/Button.js';
import {TRANSITIONS} from '../../data/transitions.js';

// The same words as the TV while the announcer explains the next part. The first player can skip.
export class TuringTownPlayerTransition extends PlayerStage {
    constructor() {
        super('TuringTownPlayerTransition');
    }

    build() {
        this.timerText.setVisible(false);
        this.title = this.addText('', {fontStyle: 'bold', align: 'center'}).setColor(COLORS.gold);
        this.captionText = this.addText('', {align: 'center'});
        this.detailText = this.addText('', {align: 'center', fontStyle: 'bold'});
        this.skipBtn = createTextButton(this, 0, 0, 'Skip', () => this.skip(), {width: 220, height: 60, fontSize: '26px'});
        this.skipBtn.setVisible(!!this.session.you?.leader);

        this.listen(this.session, 'state', () => this.showBeat());
        this.showBeat();
    }

    showBeat() {
        const {data, stage} = this.session.state;
        const step = TRANSITIONS[data?.beat];
        if (stage !== 'Transition' || !step) return;
        this.title.setText(step.title);
        this.captionText.setText(step.caption);

        let detail = data.hostLine ?? '';
        if (data.hotSeat) {
            detail = data.hotSeat.id === this.session.you?.id ? 'It is YOU.' : data.hotSeat.alias;
            if (data.beat === 'answer') detail += `\n${data.topic}\n"${data.answer ?? 'said nothing'}"`;
        }
        this.detailText.setText(detail);
    }

    skip() {
        this.session.invoke('TransitionDone', this.session.state.data?.beat).catch(err => console.warn(hubErrorMessage(err)));
    }

    layout(m) {
        const wrap = m.panelW - 60 * m.s;
        this.title.setPosition(m.cx, m.top + 140 * m.s).setFontSize(m.fs(36, 20)).setWordWrapWidth(wrap);
        this.captionText.setPosition(m.cx, m.top + 280 * m.s).setFontSize(m.fs(26, 16)).setWordWrapWidth(wrap);
        this.detailText.setPosition(m.cx, m.top + 460 * m.s).setFontSize(m.fs(28, 16)).setWordWrapWidth(wrap);
        this.skipBtn.setPosition(m.cx, m.bottom - 70 * m.s);
    }
}
