import {PlayerStage} from '../../../../core/stage/PlayerStage.js';
import {hubErrorMessage} from '../../../../core/gameHub.js';
import {createTextButton} from '../../../../core/ui/Button.js';
import {COLORS} from '../../../../core/theme.js';

/**
 * A simple phone screen for the night's own stages: a title, a few lines, and (for the leader only, when
 * `continueLabel` is set) a button that moves the night on. Subclasses implement `lines()`, which returns
 * {title, body, detail} from the state, and may set `continueLabel`.
 */
export class NightScreen extends PlayerStage {
    build() {
        this.titleText = this.addText('', {fontStyle: 'bold', align: 'center'});
        this.bodyText = this.addText('', {align: 'center'});
        this.detailText = this.addText('', {align: 'center'}).setColor(COLORS.gold);
        this.errorText = this.addText('', {align: 'center'}).setColor(COLORS.danger);

        this.continueBtn = createTextButton(this, 0, 0, this.continueLabel ?? 'Continue', () => this.continue(),
            {width: 300, height: 64, fontSize: '28px', variant: 'primary'});

        this.listen(this.session, 'state', () => this.show());
        this.listen(this.session, 'roster', () => this.show());
        this.show();
    }

    /** {title, body, detail} for the current state. */
    lines() {
        return {title: '', body: '', detail: ''};
    }

    show() {
        const {title = '', body = '', detail = ''} = this.lines(this.session.state.data ?? {});
        this.titleText.setText(title);
        this.bodyText.setText(body);
        this.detailText.setText(detail);
        this.continueBtn.setVisible(!!this.continueLabel && !!this.session.you?.leader);
    }

    continue() {
        this.continueBtn.setEnabled(false);
        this.errorText.setText('');
        this.session.invoke('ContinueNight').catch(err => {
            this.continueBtn.setEnabled(true);
            this.errorText.setText(hubErrorMessage(err));
        });
    }

    /** The player's own points so far, from a list of standings. */
    myPoints(standings = []) {
        const mine = standings.findIndex(s => s.playerId === this.session.you?.id);
        return mine < 0 ? null : {rank: mine + 1, points: standings[mine].points, of: standings.length};
    }

    layout(m) {
        const wrap = m.panelW - 60 * m.s;
        this.titleText.setPosition(m.cx, m.cy - 150 * m.s).setFontSize(m.fs(42, 22)).setWordWrapWidth(wrap);
        this.bodyText.setPosition(m.cx, m.cy - 30 * m.s).setFontSize(m.fs(26, 16)).setWordWrapWidth(wrap);
        this.detailText.setPosition(m.cx, m.cy + 80 * m.s).setFontSize(m.fs(26, 16)).setWordWrapWidth(wrap);
        this.continueBtn.setPosition(m.cx, m.cy + 190 * m.s);
        this.errorText.setPosition(m.cx, m.cy + 255 * m.s).setFontSize(m.fs(18, 14)).setWordWrapWidth(wrap);
    }
}
