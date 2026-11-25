import {HOST_CENTER_X} from '../../../../constants.js';
import {HostStage} from '../../../../core/stage/HostStage.js';
import {textStyle} from '../../../../core/theme.js';
import {createTable} from './table.js';

// The hot topic: everyone answers on their phone. Only the answer shown on the hot seat is ever seen.
export class TuringTownHostHotTopic extends HostStage {
    constructor() {
        super('TuringTownHostHotTopic');
    }

    build() {
        this.setTitle('One more question');
        this.setInstruction('Answer on your phone');
        this.add.text(HOST_CENTER_X, 190, this.session.state.data?.topic ?? '', textStyle('title', {
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
