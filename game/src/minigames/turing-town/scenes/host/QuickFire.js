import {HOST_CENTER_X} from '../../../../constants.js';
import {HostStage} from '../../../../core/stage/HostStage.js';
import {textStyle} from '../../../../core/theme.js';
import {roundTitle} from '../../rounds.js';
import {createTable} from './table.js';

// Round 1 opener: one prompt on the TV, everyone answers on their phone. The answers stay hidden until the chat opens.
export class TuringTownHostQuickFire extends HostStage {
    constructor() {
        super('TuringTownHostQuickFire');
    }

    build() {
        this.setTitle(roundTitle(this.session.state));
        this.setInstruction('Answer on your phone');
        this.add.text(HOST_CENTER_X, 190, this.session.state.data?.prompt ?? '', textStyle('title', {
            fontSize: '36px', align: 'center', wordWrap: {width: 1100},
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
