import {HOST_CENTER_X} from '../../../../constants.js';
import {HostStage} from '../../../../core/stage/HostStage.js';
import {textStyle} from '../../../../core/theme.js';
import {roundTitle} from '../../rounds.js';
import {createTable} from './table.js';

// Five questions on the TV while everyone answers on their phone. No answers are shown until the vote.
export class TuringTownHostRapidFire extends HostStage {
    constructor() {
        super('TuringTownHostRapidFire');
    }

    build() {
        this.setTitle(roundTitle(this.session.state));
        this.setInstruction('Answer all five on your phone. Everyone will see them.');
        const questions = this.session.state.data?.questions ?? [];
        this.add.text(HOST_CENTER_X, 230, questions.map((q, i) => `${i + 1}. ${q}`).join('\n'), textStyle('instruction', {
            fontSize: '26px', align: 'left', wordWrap: {width: 1100},
        })).setOrigin(0.5);

        createTable(this);

        this.listen(this.session, 'progress', () => this.showProgress());
        this.showProgress();
    }

    showProgress() {
        const {done, total} = this.session.progress;
        this.setProgress(done, total);
    }
}
