import {HOST_CENTER_X, HOST_CENTER_Y} from '../../../../constants.js';
import {HostStage} from '../../../../core/stage/HostStage.js';
import {COLORS, textStyle} from '../../../../core/theme.js';

// Everyone writes on their phone. The answers stay hidden until their matchup, so the TV only shows progress.
export class WittyBitsHostWriting extends HostStage {
    constructor() {
        super('WittyBitsHostWriting');
    }

    build() {
        const {round, lastBit} = this.session.state.data ?? {};
        this.setTitle(lastBit ? 'The Last Bit' : `Round ${round}`);
        this.setInstruction(lastBit ? 'Everyone answers the same prompt on their phone' : 'Write something funny on your phone');

        const hint = lastBit
            ? 'Every vote you get is worth points.'
            : round > 1 ? 'Points are doubled this round!' : 'Two prompts each. Make them laugh.';
        this.add.text(HOST_CENTER_X, HOST_CENTER_Y - 90, hint, textStyle('bodyOnArt', {fontSize: '30px', color: COLORS.gold})).setOrigin(0.5);

        if (lastBit) {
            const prompt = this.session.state.data?.assignments?.[0]?.text ?? '';
            this.add.text(HOST_CENTER_X, HOST_CENTER_Y - 20, prompt, textStyle('title', {fontSize: '34px', align: 'center', wordWrap: {width: 1000}})).setOrigin(0.5);
        }

        this.listen(this.session, 'progress', () => this.showProgress());
        this.showProgress();
        this.createRosterRow(HOST_CENTER_Y + 190);
    }

    showProgress() {
        const {done, total} = this.session.progress;
        this.setProgress(done, total);
    }
}
